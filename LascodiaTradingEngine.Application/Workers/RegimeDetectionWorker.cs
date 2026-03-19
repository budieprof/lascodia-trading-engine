using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically detects the market regime for each
/// active currency pair across a set of standard timeframes.
/// </summary>
public class RegimeDetectionWorker : BackgroundService
{
    private readonly ILogger<RegimeDetectionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMarketRegimeDetector _regimeDetector;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);
    private static readonly Timeframe[] Timeframes = [Timeframe.H1, Timeframe.H4, Timeframe.D1];
    private const int CandleLookback = 50;

    public RegimeDetectionWorker(
        ILogger<RegimeDetectionWorker> logger,
        IServiceScopeFactory scopeFactory,
        IMarketRegimeDetector regimeDetector)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _regimeDetector = regimeDetector;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RegimeDetectionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DetectAllRegimesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in RegimeDetectionWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("RegimeDetectionWorker stopped");
    }

    private async Task DetectAllRegimesAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var pairs = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(p => p.IsActive && !p.IsDeleted)
            .ToListAsync(ct);

        foreach (var pair in pairs)
        {
            foreach (var timeframe in Timeframes)
            {
                ct.ThrowIfCancellationRequested();
                await DetectAsync(pair.Symbol, timeframe, readContext, writeContext, ct);
            }
        }
    }

    private async Task DetectAsync(
        string symbol,
        Timeframe timeframe,
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        CancellationToken ct)
    {
        try
        {
            var candles = await readContext.GetDbContext()
                .Set<Candle>()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe && c.IsClosed && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(CandleLookback)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < 21)
            {
                _logger.LogDebug(
                    "RegimeDetectionWorker: insufficient candles for {Symbol}/{Timeframe} ({Count})",
                    symbol, timeframe, candles.Count);
                return;
            }

            var snapshot = await _regimeDetector.DetectAsync(symbol, timeframe, candles, ct);

            await writeContext.GetDbContext()
                .Set<MarketRegimeSnapshot>()
                .AddAsync(snapshot, ct);

            await writeContext.SaveChangesAsync(ct);

            _logger.LogInformation(
                "RegimeDetectionWorker: {Symbol}/{Timeframe} → {Regime} (confidence={Confidence:F2}, ADX={ADX:F2})",
                symbol, timeframe, snapshot.Regime, snapshot.Confidence, snapshot.ADX);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RegimeDetectionWorker: failed to detect regime for {Symbol}/{Timeframe}", symbol, timeframe);
        }
    }
}
