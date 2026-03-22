using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Commands.IngestCandle;
using LascodiaTradingEngine.Application.MarketData.Commands.UpdateLiveCandle;
using LascodiaTradingEngine.Application.Services.BrokerAdapters;
using LascodiaTradingEngine.Application.Services.MarketData;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that connects to the broker data feed, streams live ticks into the
/// in-memory price cache, and persists closed candles to the database.
/// </summary>
/// <remarks>
/// <b>Role in the trading engine:</b>
/// <c>MarketDataWorker</c> is the entry point for all real-time market data. It sits at the
/// start of the data pipeline and feeds downstream workers (strategy evaluation, regime
/// detection, risk monitoring) with up-to-date price information.
///
/// <b>Data flow:</b>
/// <list type="number">
///   <item>On startup, queries the read DB for all active, non-deleted <see cref="Domain.Entities.CurrencyPair"/> symbols.</item>
///   <item>Calls <see cref="IBrokerDataFeed.SubscribeAsync"/> with the symbol list and a tick callback.</item>
///   <item>For every incoming tick, dispatches <see cref="UpdateLiveCandleCommand"/> via MediatR,
///         which updates the <see cref="ILivePriceCache"/> and publishes a <c>PriceUpdatedIntegrationEvent</c>
///         to the event bus so strategy workers can react immediately.</item>
/// </list>
///
/// <b>Polling model:</b>
/// Unlike most other workers, <c>MarketDataWorker</c> is <em>push-based</em> rather than
/// poll-based. <see cref="IBrokerDataFeed.SubscribeAsync"/> blocks for the lifetime of the
/// connection and invokes <see cref="OnTickAsync"/> on every price update received from the
/// broker. The method only returns when the <paramref name="stoppingToken"/> is cancelled.
///
/// <b>Idle behaviour:</b>
/// If no active currency pairs are found at startup, the worker logs a warning and exits
/// without subscribing. This prevents unnecessary broker connections during initial setup
/// or when all pairs have been deactivated.
///
/// <b>Dependency notes:</b>
/// <list type="bullet">
///   <item><see cref="IBrokerDataFeed"/> is injected directly (Singleton) because the subscription
///         connection must outlive any individual DI scope.</item>
///   <item>Scoped services (MediatR, DbContext) are resolved per-tick via
///         <see cref="IServiceScopeFactory"/> to respect EF Core scope boundaries.</item>
/// </list>
/// </remarks>
public class MarketDataWorker : BackgroundService
{
    private readonly ILogger<MarketDataWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// The broker data feed used to subscribe to live price streams.
    /// Injected as Singleton so the feed connection is held for the full application lifetime.
    /// </summary>
    private readonly IBrokerDataFeed _feed;

    /// <summary>
    /// Aggregates incoming ticks into OHLCV candle bars across all timeframes.
    /// When a tick crosses into a new time period, the previous candle is closed
    /// and persisted via <see cref="IngestCandleCommand"/>.
    /// </summary>
    private readonly ICandleAggregator _candleAggregator;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for diagnostic and operational messages.</param>
    /// <param name="scopeFactory">
    /// Factory used to create short-lived DI scopes for each tick callback,
    /// ensuring scoped services (MediatR, EF Core DbContext) are properly disposed after each call.
    /// </param>
    /// <param name="feed">
    /// The broker data feed adapter. Must implement <see cref="IBrokerDataFeed.SubscribeAsync"/>
    /// as a long-running push subscription (e.g. WebSocket or streaming REST).
    /// </param>
    /// <param name="candleAggregator">
    /// Tick-to-candle aggregator. Accumulates ticks and emits closed candles when a time
    /// period boundary is crossed.
    /// </param>
    public MarketDataWorker(
        ILogger<MarketDataWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBrokerDataFeed feed,
        ICandleAggregator candleAggregator)
    {
        _logger           = logger;
        _scopeFactory     = scopeFactory;
        _feed             = feed;
        _candleAggregator = candleAggregator;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure.
    /// Resolves active symbols, then blocks on the broker feed subscription until cancellation.
    /// </summary>
    /// <param name="stoppingToken">
    /// Signalled when the host is shutting down. Passed to <see cref="IBrokerDataFeed.SubscribeAsync"/>
    /// so the feed adapter can cleanly close the underlying connection.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataWorker starting");

        // Resolve the symbol list before entering the subscription loop.
        // This is a one-time read; if pairs change at runtime, a restart is required
        // (or a future enhancement could re-query and re-subscribe dynamically).
        var symbols = await GetActiveSymbolsAsync(stoppingToken);

        if (symbols.Count == 0)
        {
            // Guard against an empty DB state — do not attempt to subscribe with no symbols,
            // as broker APIs typically reject empty subscription requests.
            _logger.LogWarning("No active currency pairs found — MarketDataWorker idle");
            return;
        }

        _logger.LogInformation("Subscribing to {Count} symbols: {Symbols}",
            symbols.Count, string.Join(", ", symbols));

        // SubscribeAsync is a long-running call — it holds the connection open and
        // fires OnTickAsync for every price update until stoppingToken is cancelled.
        await _feed.SubscribeAsync(symbols, OnTickAsync, stoppingToken);

        // Flush any in-progress candles so the currently building bars are not lost.
        await FlushOpenCandlesAsync();

        _logger.LogInformation("MarketDataWorker stopped");
    }

    /// <summary>
    /// Callback invoked by the broker feed adapter for every incoming price tick.
    /// Creates a short-lived DI scope and dispatches a <see cref="UpdateLiveCandleCommand"/>
    /// to update the live price cache and notify downstream consumers via the event bus.
    /// </summary>
    /// <param name="tick">
    /// The incoming tick containing the symbol, bid price, ask price, and UTC timestamp.
    /// </param>
    /// <remarks>
    /// A fresh DI scope is created per tick so that scoped services (MediatR handlers,
    /// EF Core DbContext) are allocated and disposed cleanly on each invocation.
    /// This avoids DbContext threading issues that would arise from reusing a single scope
    /// across concurrent or sequential async callbacks.
    /// </remarks>
    private async Task OnTickAsync(Tick tick)
    {
        // Create a new DI scope for this tick to ensure scoped EF Core contexts
        // and MediatR pipeline behaviours are properly isolated and disposed.
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Dispatch the update command through the MediatR pipeline.
        // The handler for UpdateLiveCandleCommand will:
        //   1. Persist the mid-price to ILivePriceCache (in-memory or DB-backed).
        //   2. Publish a PriceUpdatedIntegrationEvent so StrategyWorker and
        //      other subscribers can react to the new price without polling.
        await mediator.Send(new UpdateLiveCandleCommand
        {
            Symbol    = tick.Symbol,
            Bid       = tick.Bid,
            Ask       = tick.Ask,
            Timestamp = tick.Timestamp
        });

        // Aggregate the tick into OHLCV candle bars across all timeframes.
        // When the tick crosses a period boundary, the aggregator returns the
        // closed candle(s) which are then persisted via IngestCandleCommand.
        var closedCandles = _candleAggregator.ProcessTick(tick);

        foreach (var candle in closedCandles)
        {
            await mediator.Send(new IngestCandleCommand
            {
                Symbol    = candle.Symbol,
                Timeframe = candle.Timeframe.ToString(),
                Open      = candle.Open,
                High      = candle.High,
                Low       = candle.Low,
                Close     = candle.Close,
                Volume    = candle.TickVolume,
                Timestamp = candle.Timestamp,
                IsClosed  = true
            });
        }
    }

    /// <summary>
    /// Queries the read database for symbols of all active, non-deleted currency pairs.
    /// Called once at startup to build the subscription list passed to the broker feed.
    /// </summary>
    /// <param name="cancellationToken">Propagated from the host stopping token.</param>
    /// <returns>
    /// A list of ticker symbols (e.g. <c>["EURUSD", "GBPUSD", "USDJPY"]</c>).
    /// Returns an empty list if no active pairs are configured, which causes the worker
    /// to enter its idle state without attempting a broker subscription.
    /// </returns>
    /// <summary>
    /// Flushes all in-progress candles from the aggregator and persists them via
    /// <see cref="IngestCandleCommand"/>. Called on graceful shutdown so the currently
    /// building bars are not lost.
    /// </summary>
    private async Task FlushOpenCandlesAsync()
    {
        var flushedCandles = _candleAggregator.FlushAll();

        if (flushedCandles.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        foreach (var candle in flushedCandles)
        {
            await mediator.Send(new IngestCandleCommand
            {
                Symbol    = candle.Symbol,
                Timeframe = candle.Timeframe.ToString(),
                Open      = candle.Open,
                High      = candle.High,
                Low       = candle.Low,
                Close     = candle.Close,
                Volume    = candle.TickVolume,
                Timestamp = candle.Timestamp,
                IsClosed  = false
            });
        }
    }

    private async Task<List<string>> GetActiveSymbolsAsync(CancellationToken cancellationToken)
    {
        // Use a dedicated scope because GetActiveSymbolsAsync is called before the main
        // subscription loop — it must not share a context with the long-running feed connection.
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // EF global query filter automatically excludes IsDeleted rows;
        // IsActive is an additional domain filter that allows pairs to be temporarily
        // suspended without removing them from the database.
        return await context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);
    }
}
