using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Sentiment.Commands.RecordSentiment;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically fetches market sentiment scores for all active
/// currency pairs and persists a <see cref="SentimentSnapshot"/> record per currency.
/// </summary>
/// <remarks>
/// Runs every 4 hours. Sentiment data is sourced from the configured sentiment feed
/// (e.g. retail positioning from a broker API, social-media sentiment indices, or a
/// commercial sentiment data provider). Until a live feed is wired up, a stub
/// implementation returns placeholder scores so the rest of the system can operate.
///
/// The <see cref="SentimentSnapshot.SentimentScore"/> uses a −1.0 to +1.0 scale:
/// +1.0 is maximally bullish (all traders long), −1.0 is maximally bearish (all short).
/// </remarks>
public class SentimentWorker : BackgroundService
{
    private readonly ILogger<SentimentWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(4);

    public SentimentWorker(ILogger<SentimentWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SentimentWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IngestSentimentAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in SentimentWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("SentimentWorker stopped");
    }

    private async Task IngestSentimentAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load active currency pairs to determine which symbols need sentiment data
        var activePairs = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (activePairs.Count == 0)
        {
            _logger.LogDebug("SentimentWorker: no active currency pairs found, skipping");
            return;
        }

        _logger.LogInformation(
            "SentimentWorker: fetching sentiment for {Count} currency pairs", activePairs.Count);

        foreach (var pair in activePairs)
        {
            try
            {
                // Fetch sentiment data — stub values used until a live feed is wired up.
                // Replace with a real HTTP call to your sentiment data vendor.
                var (score, bullish, bearish, neutral) = await FetchSentimentStubAsync(pair.Symbol, ct);

                await mediator.Send(new RecordSentimentCommand
                {
                    Symbol         = pair.Symbol,
                    Source         = "AutoFeed",
                    SentimentScore = score,
                    BullishPct     = bullish,
                    BearishPct     = bearish,
                    NeutralPct     = neutral
                }, ct);

                _logger.LogDebug(
                    "SentimentWorker: recorded sentiment for {Symbol} — Score={Score:F2}, Bullish={Bull:P0}, Bearish={Bear:P0}",
                    pair.Symbol, score, bullish, bearish);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SentimentWorker: failed to record sentiment for {Symbol}", pair.Symbol);
            }
        }
    }

    /// <summary>
    /// Stub sentiment fetcher. Replace with a real data vendor HTTP call.
    /// Returns (SentimentScore −1..+1, BullishPct 0..1, BearishPct 0..1, NeutralPct 0..1).
    /// </summary>
    private static Task<(decimal score, decimal bullish, decimal bearish, decimal neutral)> FetchSentimentStubAsync(
        string symbol, CancellationToken ct)
    {
        // Stub: generate a neutral-ish score for development; replace with real data.
        var rng      = new Random(symbol.GetHashCode() ^ DateTime.UtcNow.Hour);
        decimal bull = (decimal)(0.3 + rng.NextDouble() * 0.4);    // 30–70%
        decimal bear = (decimal)Math.Min(1.0 - (double)bull, 0.6);
        decimal neutral = Math.Max(0m, 1m - bull - bear);
        decimal score   = Math.Round(bull - bear, 4);

        return Task.FromResult((score, Math.Round(bull, 4), Math.Round(bear, 4), Math.Round(neutral, 4)));
    }
}
