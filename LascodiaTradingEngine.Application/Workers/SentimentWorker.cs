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
/// currency pairs via the configured <see cref="ISentimentFeed"/> and persists a
/// <see cref="SentimentSnapshot"/> record per currency.
/// </summary>
/// <remarks>
/// <b>Role in the trading engine:</b>
/// Sentiment data represents the aggregate directional bias of market participants.
/// It is used as a confirmatory or contrarian signal filter within strategy evaluation:
/// strategies may require sentiment alignment before entering a trade, or may use
/// extreme sentiment readings as a contrarian signal (crowded trades tend to reverse).
/// The ML signal scorer also consumes sentiment snapshots as input features.
///
/// <b>Polling cadence:</b>
/// Runs every 4 hours (<see cref="PollingInterval"/>). Sentiment data changes slowly
/// relative to price data — retail positioning surveys and social-media indices update
/// on an hourly-to-daily basis — so 4-hour polling balances freshness against
/// unnecessary API calls to the sentiment data provider.
///
/// <b>Data sources:</b>
/// Sentiment data is sourced from the registered <see cref="ISentimentFeed"/>
/// implementation, which can be:
/// <list type="bullet">
///   <item>Retail positioning data from a broker API (e.g. OANDA's Open Position Ratios).</item>
///   <item>Social-media sentiment indices (e.g. Twitter/Reddit NLP scoring).</item>
///   <item>A commercial sentiment data provider (e.g. MarketPsych, Refinitiv).</item>
///   <item>DeepSeek NLP analysis of recent headlines via <see cref="IDeepSeekSentimentService"/>.</item>
/// </list>
///
/// <b>Sentiment score scale:</b>
/// <see cref="SentimentSnapshot.SentimentScore"/> uses a −1.0 to +1.0 scale:
/// +1.0 is maximally bullish (all retail traders long), −1.0 is maximally bearish
/// (all traders short). This is computed as <c>BullishPct − BearishPct</c>.
///
/// <b>Error isolation:</b>
/// Failures for individual currency pairs are caught and logged independently so a
/// single bad symbol does not prevent sentiment ingestion for the rest of the portfolio.
/// </remarks>
public class SentimentWorker : BackgroundService
{
    private readonly ILogger<SentimentWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(4);
    private int _consecutiveFailures;

    public SentimentWorker(
        ILogger<SentimentWorker> logger,
        IServiceScopeFactory scopeFactory)
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
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "SentimentWorker polling error (failure #{Count})", _consecutiveFailures);
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("SentimentWorker stopped");
    }

    private async Task IngestSentimentAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sentimentFeed = scope.ServiceProvider.GetRequiredService<ISentimentFeed>();

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
            "SentimentWorker: fetching sentiment for {Count} currency pairs via {Feed}",
            activePairs.Count, sentimentFeed.SourceName);

        int ingested = 0, skipped = 0, failed = 0;

        foreach (var pair in activePairs)
        {
            try
            {
                var reading = await sentimentFeed.FetchAsync(pair.Symbol, ct);

                if (reading is null)
                {
                    skipped++;
                    _logger.LogDebug("SentimentWorker: no data available for {Symbol}, skipping", pair.Symbol);
                    continue;
                }

                await mediator.Send(new RecordSentimentCommand
                {
                    Symbol         = pair.Symbol,
                    Source         = sentimentFeed.SourceName,
                    SentimentScore = reading.SentimentScore,
                    BullishPct     = reading.BullishPct,
                    BearishPct     = reading.BearishPct,
                    NeutralPct     = reading.NeutralPct
                }, ct);

                ingested++;

                _logger.LogDebug(
                    "SentimentWorker: recorded sentiment for {Symbol} — Score={Score:F2}, Bullish={Bull:P0}, Bearish={Bear:P0}",
                    pair.Symbol, reading.SentimentScore, reading.BullishPct, reading.BearishPct);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "SentimentWorker: failed to record sentiment for {Symbol}", pair.Symbol);
            }
        }

        _logger.LogInformation(
            "SentimentWorker: ingested={Ingested}, skipped={Skipped}, failed={Failed}",
            ingested, skipped, failed);
    }
}
