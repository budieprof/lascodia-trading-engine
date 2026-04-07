using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically detects the market regime for each
/// active currency pair across a set of standard timeframes.
/// </summary>
/// <remarks>
/// <b>Role in the trading engine:</b>
/// Market regime classification is a prerequisite for strategy selection and ML model
/// accuracy. Strategies that perform well in trending markets (e.g. momentum) typically
/// underperform in ranging or volatile conditions. By continuously classifying each
/// symbol/timeframe combination, the engine allows <c>StrategyWorker</c>, the ML scorer,
/// and risk management to adapt their behaviour dynamically.
///
/// <b>Polling cadence:</b>
/// Runs every 60 seconds (<see cref="PollingInterval"/>). On each tick, it processes
/// every active <see cref="CurrencyPair"/> across three timeframes (<c>H1</c>, <c>H4</c>,
/// <c>D1</c>), giving multi-timeframe regime awareness that helps filter false signals on
/// lower timeframes when the higher-timeframe context is unfavourable.
///
/// <b>Detection algorithm:</b>
/// Delegates to <see cref="IMarketRegimeDetector.DetectAsync"/>, which uses a combination
/// of ADX (Average Directional Index), Bollinger Band width, and volatility percentile to
/// classify the regime as <see cref="MarketRegime.Trending"/>, <see cref="MarketRegime.Ranging"/>,
/// or <see cref="MarketRegime.Volatile"/>. The resulting <see cref="MarketRegimeSnapshot"/>
/// captures the regime, confidence score, and raw ADX value for audit and query purposes.
///
/// <b>Minimum data requirement:</b>
/// At least 21 closed candles are required per symbol/timeframe. This is the minimum window
/// needed for a statistically meaningful ADX calculation (14-period ADX needs at least
/// 14 + some warmup candles). Pairs or timeframes with insufficient history are skipped
/// with a debug log rather than failing the entire pass.
///
/// <b>Persistence:</b>
/// Each detected snapshot is inserted as a new <see cref="MarketRegimeSnapshot"/> row.
/// Snapshots are append-only (no upsert) — historical regime data is valuable for
/// ML training retrospectives and walk-forward analysis.
///
/// <b>Error isolation:</b>
/// Failures for individual symbol/timeframe combinations are caught and logged as warnings
/// without interrupting the rest of the detection pass, ensuring a single bad symbol does
/// not block regime classification for the remainder of the portfolio.
/// </remarks>
public class RegimeDetectionWorker : BackgroundService
{
    private readonly ILogger<RegimeDetectionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// The regime detector implementation that performs the actual classification logic.
    /// Injected as Singleton because the detector holds no per-request state.
    /// </summary>
    private readonly IMarketRegimeDetector _regimeDetector;

    /// <summary>How frequently the worker wakes up to run a full regime detection pass.</summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    /// <summary>Shorter polling interval used during high-volatility or crisis regimes.</summary>
    private static readonly TimeSpan HighVolPollingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Tracks the most recently detected regime to enable dynamic interval adjustment.</summary>
    private MarketRegimeEnum? _latestDetectedRegime;

    /// <summary>
    /// Timeframes evaluated on each polling cycle.
    /// H1 catches intraday regime shifts; H4 and D1 provide mid- and long-term context.
    /// Using multiple timeframes enables regime confluence checks in strategy filters.
    /// </summary>
    private static readonly Timeframe[] Timeframes = [Timeframe.H1, Timeframe.H4, Timeframe.D1];

    /// <summary>
    /// Number of recent closed candles fetched per symbol/timeframe combination.
    /// 50 candles provides sufficient history for a stable ADX reading while keeping
    /// the DB query lightweight.
    /// </summary>
    private const int CandleLookback = 50;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for operational and diagnostic messages.</param>
    /// <param name="scopeFactory">
    /// Factory used to create per-cycle DI scopes. Read and write DbContexts are
    /// resolved within a single scope per detection pass so they share the same
    /// underlying database connection.
    /// </param>
    /// <param name="regimeDetector">
    /// The stateless regime classification service. Receives a candle series and returns
    /// a fully-populated <see cref="MarketRegimeSnapshot"/>.
    /// </param>
    public RegimeDetectionWorker(
        ILogger<RegimeDetectionWorker> logger,
        IServiceScopeFactory scopeFactory,
        IMarketRegimeDetector regimeDetector)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _regimeDetector = regimeDetector;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure.
    /// Runs a continuous polling loop, calling <see cref="DetectAllRegimesAsync"/> on each cycle.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
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
                // Expected during graceful shutdown — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Catch-all so a transient error (e.g. DB timeout) does not kill the worker.
                // The next cycle will retry automatically after PollingInterval.
                _logger.LogError(ex, "Unexpected error in RegimeDetectionWorker polling loop");
            }

            // Dynamic interval: faster during high volatility or crisis regimes
            var effectiveInterval = _latestDetectedRegime is MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Crisis
                ? HighVolPollingInterval
                : PollingInterval;

            // Wait before the next detection pass. Task.Delay respects cancellation so
            // the worker shuts down promptly even mid-wait.
            await Task.Delay(effectiveInterval, stoppingToken);
        }

        _logger.LogInformation("RegimeDetectionWorker stopped");
    }

    /// <summary>
    /// Loads all active currency pairs and runs regime detection for every
    /// symbol/timeframe combination. Each detection result is persisted as a new snapshot.
    /// </summary>
    /// <param name="ct">Propagated cancellation token; checked between each symbol/timeframe.</param>
    /// <remarks>
    /// Read and write contexts are resolved within the same DI scope so they can participate
    /// in the same database transaction if needed. The read context is used exclusively for
    /// querying candle history; the write context is used for persisting snapshots.
    /// </remarks>
    private async Task DetectAllRegimesAsync(CancellationToken ct)
    {
        // Create a single scope for the entire detection pass so all DB work within
        // one cycle shares the same connection pool slot.
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // Fetch all active pairs up front to avoid N+1 queries in the inner loops.
        var pairs = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(p => p.IsActive && !p.IsDeleted)
            .ToListAsync(ct);

        foreach (var pair in pairs)
        {
            foreach (var timeframe in Timeframes)
            {
                // Honour cancellation between each symbol/timeframe so the worker
                // can shut down quickly even when processing a large portfolio.
                ct.ThrowIfCancellationRequested();
                await DetectAsync(pair.Symbol, timeframe, readContext, writeContext, ct);
            }
        }
    }

    /// <summary>
    /// Runs regime detection for a single symbol/timeframe combination and persists
    /// the resulting <see cref="MarketRegimeSnapshot"/> to the write database.
    /// </summary>
    /// <param name="symbol">The currency pair symbol, e.g. <c>"EURUSD"</c>.</param>
    /// <param name="timeframe">The candle timeframe to evaluate (H1, H4, or D1).</param>
    /// <param name="readContext">Read DbContext used to query historical candle data.</param>
    /// <param name="writeContext">Write DbContext used to persist the regime snapshot.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task DetectAsync(
        string symbol,
        Timeframe timeframe,
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        CancellationToken ct)
    {
        try
        {
            // Fetch the most recent N closed candles for this symbol/timeframe.
            // The double-sort (descending → take → ascending) is intentional:
            //   - OrderByDescending + Take(N) efficiently retrieves the latest N rows via index.
            //   - Re-ordering ascending ensures the candle array is chronological for indicator math.
            var candles = await readContext.GetDbContext()
                .Set<Candle>()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe && c.IsClosed && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(CandleLookback)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            // Enforce a minimum candle count. 21 is the practical floor for a meaningful
            // ADX reading (14-period smoothed directional movement needs warmup bars).
            // Symbols that have just been added to the engine will be skipped until
            // enough history has been ingested by MarketDataWorker.
            if (candles.Count < 21)
            {
                _logger.LogDebug(
                    "RegimeDetectionWorker: insufficient candles for {Symbol}/{Timeframe} ({Count})",
                    symbol, timeframe, candles.Count);
                return;
            }

            // Delegate to the injected detector for the actual classification logic.
            // The returned snapshot contains the regime label, a confidence score (0–1),
            // and the raw ADX value used for classification.
            var snapshot = await _regimeDetector.DetectAsync(symbol, timeframe, candles, ct);

            // Persist the snapshot as a new append-only row.
            // Historical snapshots are intentionally retained for ML training and audit.
            await writeContext.GetDbContext()
                .Set<MarketRegimeSnapshot>()
                .AddAsync(snapshot, ct);

            await writeContext.SaveChangesAsync(ct);

            // Track the latest detected regime for dynamic interval adjustment
            _latestDetectedRegime = snapshot.Regime;

            _logger.LogInformation(
                "RegimeDetectionWorker: {Symbol}/{Timeframe} → {Regime} (confidence={Confidence:F2}, ADX={ADX:F2})",
                symbol, timeframe, snapshot.Regime, snapshot.Confidence, snapshot.ADX);
        }
        catch (Exception ex)
        {
            // Log as Warning rather than Error — a single symbol failure is non-critical
            // and should not disrupt the rest of the detection pass.
            _logger.LogWarning(ex,
                "RegimeDetectionWorker: failed to detect regime for {Symbol}/{Timeframe}", symbol, timeframe);
        }
    }
}
