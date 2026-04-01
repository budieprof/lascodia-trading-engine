using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Computes symbol-specific gap risk multipliers from historical Monday open vs Friday close data.
/// Caches calibrated multipliers in memory and recalibrates weekly.
/// Replaces the static 1.5x weekend gap multiplier in RiskChecker.
/// Registered as Singleton — uses IServiceScopeFactory to avoid captive DbContext dependency.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class GapRiskModel : IGapRiskModel
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GapRiskModel> _logger;

    /// <summary>Cached gap estimates keyed by symbol. Recalibrated weekly.</summary>
    private readonly ConcurrentDictionary<string, GapRiskEstimate> _cache = new();

    /// <summary>Default multiplier when insufficient data exists for a symbol.</summary>
    private const decimal DefaultMultiplier = 1.5m;

    /// <summary>Minimum sample count to produce a statistically meaningful estimate.</summary>
    private const int MinSamples = 20;

    public GapRiskModel(
        IServiceScopeFactory scopeFactory,
        ILogger<GapRiskModel> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public Task<GapRiskEstimate> GetGapMultiplierAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(symbol, out var cached) &&
            (DateTime.UtcNow - cached.LastCalibrated).TotalDays < 7)
        {
            return Task.FromResult(cached);
        }

        return CalibrateSymbolAsync(symbol, cancellationToken);
    }

    public async Task RecalibrateAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var symbols = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(c => !c.IsDeleted)
            .Select(c => c.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var symbol in symbols)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await CalibrateSymbolAsync(symbol, cancellationToken);
        }

        _logger.LogInformation("GapRiskModel: recalibrated {Count} symbols", symbols.Count);
    }

    private async Task<GapRiskEstimate> CalibrateSymbolAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // Fetch D1 candles ordered by date to find Friday close → Monday open gaps
        var dailyCandles = await readContext.GetDbContext()
            .Set<Candle>()
            .Where(c => c.Symbol == symbol && c.Timeframe == Timeframe.D1
                     && c.IsClosed && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(520) // ~2 years of trading days
            .OrderBy(c => c.Timestamp)
            .ToListAsync(cancellationToken);

        var gapPcts = new List<decimal>();

        for (int i = 1; i < dailyCandles.Count; i++)
        {
            var prev = dailyCandles[i - 1];
            var curr = dailyCandles[i];

            // Detect weekend gap: >1 calendar day between bars
            if ((curr.Timestamp - prev.Timestamp).TotalDays > 1.5 && prev.Close != 0)
            {
                var gapPct = Math.Abs(curr.Open - prev.Close) / prev.Close * 100m;
                gapPcts.Add(gapPct);
            }
        }

        if (gapPcts.Count < MinSamples)
        {
            var fallback = new GapRiskEstimate(DefaultMultiplier, 0, gapPcts.Count, DateTime.UtcNow);
            _cache[symbol] = fallback;
            return fallback;
        }

        // Sort and compute P99
        gapPcts.Sort();
        var p99Index = (int)Math.Floor(0.99 * (gapPcts.Count - 1));
        var p99Gap   = gapPcts[Math.Clamp(p99Index, 0, gapPcts.Count - 1)];

        // Convert P99 gap percentage to a multiplier for risk sizing
        // A 2% P99 gap → multiplier of 2.0 (double the base risk allocation for gaps)
        var multiplier = Math.Max(1.1m, 1.0m + p99Gap / 2.0m);
        multiplier = Math.Min(multiplier, 5.0m); // Cap at 5x to prevent extreme values

        var estimate = new GapRiskEstimate(multiplier, p99Gap, gapPcts.Count, DateTime.UtcNow);
        _cache[symbol] = estimate;

        _logger.LogDebug(
            "GapRiskModel: {Symbol} calibrated — P99Gap={P99:F2}%, multiplier={Mult:F2}x, samples={N}",
            symbol, p99Gap, multiplier, gapPcts.Count);

        return estimate;
    }
}
