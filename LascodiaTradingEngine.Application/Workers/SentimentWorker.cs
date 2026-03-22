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
/// Sentiment data is sourced from the configured sentiment feed, which can be:
/// <list type="bullet">
///   <item>Retail positioning data from a broker API (e.g. OANDA's Open Position Ratios).</item>
///   <item>Social-media sentiment indices (e.g. Twitter/Reddit NLP scoring).</item>
///   <item>A commercial sentiment data provider (e.g. MarketPsych, Refinitiv).</item>
/// </list>
/// Until a live feed is wired up, <see cref="FetchSentimentStubAsync"/> returns
/// deterministic placeholder scores so the rest of the system can operate without a
/// real data subscription.
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

    /// <summary>
    /// How often the worker wakes up to collect a new round of sentiment snapshots.
    /// 4 hours balances data freshness against external API rate limits.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(4);

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for operational and diagnostic messages.</param>
    /// <param name="scopeFactory">
    /// Factory used to create short-lived DI scopes per polling cycle so that scoped
    /// services (MediatR, EF Core DbContext) are properly allocated and disposed.
    /// </param>
    public SentimentWorker(ILogger<SentimentWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure.
    /// Runs a continuous polling loop, calling <see cref="IngestSentimentAsync"/> on each cycle.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
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
                // Expected during graceful shutdown — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Catch-all so a transient error (e.g. network timeout to sentiment vendor)
                // does not kill the worker. Next cycle will retry after PollingInterval.
                _logger.LogError(ex, "Unexpected error in SentimentWorker polling loop");
            }

            // Wait for the next polling interval. Task.Delay respects cancellation so
            // the worker can shut down promptly even when mid-sleep.
            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("SentimentWorker stopped");
    }

    /// <summary>
    /// Fetches the current sentiment reading for each active currency pair and persists it
    /// as a new <see cref="SentimentSnapshot"/> via the <see cref="RecordSentimentCommand"/>.
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <remarks>
    /// A fresh DI scope is created for the entire ingestion pass. All symbol-level work
    /// is done sequentially rather than in parallel to avoid overwhelming the sentiment
    /// data provider's rate limits with concurrent HTTP calls.
    /// </remarks>
    private async Task IngestSentimentAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load active currency pairs to determine which symbols need sentiment data.
        // The EF global query filter already excludes IsDeleted rows; IsActive is an
        // additional domain filter allowing pairs to be suspended without deletion.
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
                // Replace FetchSentimentStubAsync with a real HTTP call to your sentiment
                // data vendor (e.g. broker positioning API, MarketPsych, etc.).
                var (score, bullish, bearish, neutral) = await FetchSentimentStubAsync(pair.Symbol, ct);

                // Dispatch through MediatR so the full pipeline (validation, event publishing)
                // is applied. The handler will persist the SentimentSnapshot and may publish
                // a SentimentUpdatedIntegrationEvent for downstream consumers.
                await mediator.Send(new RecordSentimentCommand
                {
                    Symbol         = pair.Symbol,
                    Source         = "AutoFeed",  // identifies this worker as the data source for audit
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
                // Isolate per-symbol failures so one bad pair does not abort the entire batch.
                _logger.LogError(ex, "SentimentWorker: failed to record sentiment for {Symbol}", pair.Symbol);
            }
        }
    }

    /// <summary>
    /// Stub sentiment fetcher used during development when no live sentiment feed is configured.
    /// Returns a tuple of <c>(SentimentScore, BullishPct, BearishPct, NeutralPct)</c>.
    /// </summary>
    /// <param name="symbol">The currency pair symbol used to seed a deterministic random value.</param>
    /// <param name="ct">Unused in the stub; included for interface compatibility with a real async HTTP call.</param>
    /// <returns>
    /// A tuple where:
    /// <list type="bullet">
    ///   <item><c>score</c> — net sentiment in the range [−1.0, +1.0] (BullishPct − BearishPct).</item>
    ///   <item><c>bullish</c> — fraction of participants holding long positions [0.0, 1.0].</item>
    ///   <item><c>bearish</c> — fraction of participants holding short positions [0.0, 1.0].</item>
    ///   <item><c>neutral</c> — remainder not classified as directional (1 − bullish − bearish).</item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// The seed combines the symbol hash with the current UTC hour so the values vary
    /// realistically across polling cycles while remaining deterministic within the same hour.
    /// This produces a neutral-ish distribution (30–70% bullish) that exercises all downstream
    /// logic without introducing extreme readings that might trigger false risk alerts.
    ///
    /// <b>Replace this method</b> with a real HTTP call before deploying to production.
    /// </remarks>
    private static Task<(decimal score, decimal bullish, decimal bearish, decimal neutral)> FetchSentimentStubAsync(
        string symbol, CancellationToken ct)
    {
        // Seed with symbol hash XOR hour so values shift hourly but are consistent
        // across multiple calls within the same polling cycle.
        var rng      = new Random(symbol.GetHashCode() ^ DateTime.UtcNow.Hour);

        // Generate a realistic bullish percentage between 30% and 70%.
        // True retail positioning rarely sits outside this range under normal conditions.
        decimal bull = (decimal)(0.3 + rng.NextDouble() * 0.4);    // 30–70%

        // Cap bearish at 60% to avoid the stub producing degenerate 0% neutral readings.
        decimal bear = (decimal)Math.Min(1.0 - (double)bull, 0.6);

        // Neutral is the remainder — traders with no directional position or undecided.
        decimal neutral = Math.Max(0m, 1m - bull - bear);

        // Score is the raw net bias: positive means more bulls than bears.
        decimal score   = Math.Round(bull - bear, 4);

        return Task.FromResult((score, Math.Round(bull, 4), Math.Round(bear, 4), Math.Round(neutral, 4)));
    }
}
