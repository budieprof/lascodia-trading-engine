using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Verifies that regime classifications are fresh before use in signal generation.
/// If the latest MarketRegimeSnapshot for a symbol/timeframe is stale (older than 2x the
/// expected update interval), flags it so the StrategyWorker can handle accordingly.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class RegimeClassificationVerifier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RegimeClassificationVerifier> _logger;

    /// <summary>Maximum age in minutes before a regime classification is considered stale.</summary>
    private const int MaxStalenessMinutes = 10;

    public RegimeClassificationVerifier(
        IServiceScopeFactory scopeFactory,
        ILogger<RegimeClassificationVerifier> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Returns true if the regime classification for this symbol/timeframe is fresh enough to use.
    /// Returns false if stale — the caller should use a neutral/conservative regime assumption.
    /// </summary>
    public async Task<bool> IsRegimeFreshAsync(
        string symbol,
        Timeframe timeframe,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var latest = await readCtx.GetDbContext()
            .Set<MarketRegimeSnapshot>()
            .Where(r => r.Symbol == symbol && r.Timeframe == timeframe && !r.IsDeleted)
            .OrderByDescending(r => r.DetectedAt)
            .Select(r => r.DetectedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest == default)
        {
            _logger.LogDebug("RegimeVerifier: no regime snapshot for {Symbol}/{Tf}", symbol, timeframe);
            return false;
        }

        var age = DateTime.UtcNow - latest;
        if (age.TotalMinutes > MaxStalenessMinutes)
        {
            _logger.LogWarning(
                "RegimeVerifier: stale regime for {Symbol}/{Tf} — age={Age:F1}min (max={Max}min)",
                symbol, timeframe, age.TotalMinutes, MaxStalenessMinutes);
            return false;
        }

        return true;
    }
}
