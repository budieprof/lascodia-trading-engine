using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that executes walk-forward analysis for queued
/// <see cref="WalkForwardRun"/> records, producing a robust, out-of-sample measure of
/// how well a strategy generalises beyond the data it was fitted on.
///
/// <para>
/// <b>What is walk-forward analysis?</b><br/>
/// Walk-forward analysis is a time-series cross-validation technique used in quantitative
/// trading to detect overfitting. Unlike a simple train/test split, the dataset is divided
/// into multiple consecutive windows. Within each window:
/// <list type="number">
///   <item>
///     An <b>in-sample (IS)</b> segment (fitting period) is used to "train" the strategy
///     — i.e. confirm that the indicator logic fires correctly given the chosen parameters.
///   </item>
///   <item>
///     An <b>out-of-sample (OOS)</b> segment immediately following the IS period is used
///     to evaluate how the strategy would have performed on data it never saw during fitting.
///   </item>
/// </list>
/// The window then slides forward by <c>OutOfSampleDays</c> candles and the process
/// repeats. Aggregating the OOS metrics across all windows yields a statistically
/// meaningful estimate of real-world performance that is far less susceptible to curve-fit
/// artefacts than a single full-period backtest.
/// </para>
///
/// <para>
/// <b>Window layout (anchored walk-forward):</b>
/// <code>
/// |&lt;--- InSampleDays ---&gt;|&lt;- OutOfSampleDays -&gt;|
///                          |&lt;--- InSampleDays ---&gt;|&lt;- OOS -&gt;|
///                                                    |&lt;--- IS --&gt;|&lt;- OOS -&gt;|
/// </code>
/// The offset advances by <c>OutOfSampleDays</c> bars on each iteration (anchored
/// walk-forward). This means each OOS period is evaluated only once, preventing
/// look-ahead bias while keeping the computation tractable.
/// </para>
///
/// <para>
/// <b>OOS metric — Sharpe Ratio:</b><br/>
/// The primary OOS quality indicator is the annualised Sharpe ratio from each window's
/// out-of-sample backtest. Sharpe is preferred over win rate or profit factor here because
/// it accounts for both return and volatility, making comparisons across windows with
/// different trade counts fairer.
/// </para>
///
/// <para>
/// <b>Aggregation:</b><br/>
/// After all windows have been processed, the worker computes:
/// <list type="bullet">
///   <item><term>AverageOutOfSampleScore</term><description>Mean Sharpe across all OOS windows — the primary quality signal.</description></item>
///   <item><term>ScoreConsistency</term><description>Sample standard deviation (Bessel's correction, N-1 denominator) of Sharpe scores — lower is more consistent.</description></item>
/// </list>
/// Both values are persisted on the <see cref="WalkForwardRun"/> row alongside the full
/// per-window breakdown in <c>WindowResultsJson</c>.
/// </para>
///
/// <para>
/// <b>Pipeline position:</b><br/>
/// WalkForwardWorker is the final stage in the validation chain. It is triggered
/// automatically by BacktestWorker (which queues a WalkForwardRun on every successful
/// backtest) and can also be triggered manually via <c>RunWalkForwardCommand</c>.
/// </para>
///
/// <para>
/// <b>Polling model:</b> Wakes every <see cref="PollingInterval"/> seconds (30 s),
/// processes one run per tick, uses per-cycle DI scopes for proper context disposal.
/// </para>
/// </summary>
public class WalkForwardWorker : BackgroundService
{
    private readonly ILogger<WalkForwardWorker> _logger;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IWalkForwardRunClaimService _runClaimService;
    private readonly IValidationSettingsProvider _settingsProvider;
    private readonly IBacktestOptionsSnapshotBuilder _optionsSnapshotBuilder;
    private readonly IStrategyExecutionSnapshotBuilder _strategySnapshotBuilder;
    private readonly IValidationCandleSeriesGuard _candleSeriesGuard;
    private readonly IValidationWorkerIdentity _workerIdentity;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Used to create per-iteration DI scopes so that scoped services such as
    /// <see cref="IWriteApplicationDbContext"/> and <see cref="IReadApplicationDbContext"/>
    /// are properly disposed after each processing cycle.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Singleton backtest engine invoked once per OOS window to simulate the strategy
    /// on the out-of-sample candle slice and return performance metrics.
    /// </summary>
    private readonly IBacktestEngine _backtestEngine;

    /// <summary>Maximum number of walk-forward runs to process concurrently.</summary>
    private const int DefaultMaxParallelWalkForwards = 4;

    /// <summary>Base polling interval when runs are available (10 seconds).</summary>
    private static readonly TimeSpan BasePollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Tracks consecutive empty polls for exponential backoff.</summary>
    private int _emptyPollStreak;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for diagnostic output.</param>
    /// <param name="scopeFactory">Factory for creating per-cycle DI scopes.</param>
    /// <param name="backtestEngine">Engine used to evaluate each OOS window.</param>
    public WalkForwardWorker(
        ILogger<WalkForwardWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine,
        IWalkForwardRunClaimService runClaimService,
        IValidationSettingsProvider settingsProvider,
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        IStrategyExecutionSnapshotBuilder strategySnapshotBuilder,
        IValidationCandleSeriesGuard candleSeriesGuard,
        IValidationWorkerIdentity workerIdentity,
        IWorkerHealthMonitor? healthMonitor = null,
        TimeProvider? timeProvider = null)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
        _runClaimService = runClaimService;
        _settingsProvider = settingsProvider;
        _optionsSnapshotBuilder = optionsSnapshotBuilder;
        _strategySnapshotBuilder = strategySnapshotBuilder;
        _candleSeriesGuard = candleSeriesGuard;
        _workerIdentity = workerIdentity;
        _healthMonitor  = healthMonitor;
        _timeProvider   = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Entry point invoked by the hosted-service runtime. Runs a continuous polling
    /// loop that claims up to <see cref="DefaultMaxParallelWalkForwards"/> queued runs,
    /// processes them concurrently via <see cref="Task.WhenAll"/>, and applies exponential
    /// backoff (10s -> 30s -> 60s) when the queue is empty.
    /// </summary>
    /// <param name="stoppingToken">
    /// Signalled by the runtime on application shutdown, causing the loop to exit
    /// gracefully once the current processing cycle completes.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WalkForwardWorker starting (parallel, max {Max} concurrent)", DefaultMaxParallelWalkForwards);
        await using (var startupScope = _scopeFactory.CreateAsyncScope())
        {
            var startupWriteDb = startupScope.ServiceProvider
                .GetRequiredService<IWriteApplicationDbContext>()
                .GetDbContext();
            _runClaimService.EnsureSupportedProvider(startupWriteDb);
        }

        _healthMonitor?.RecordWorkerMetadata(
            nameof(WalkForwardWorker),
            "Executes queued walk-forward validation runs with execution leases and retry-aware recovery.",
            BasePollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            long cycleStarted = Stopwatch.GetTimestamp();
            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(nameof(WalkForwardWorker));

                var settings = await LoadSettingsAsync(stoppingToken);
                await RecoverExpiredRunsAsync(settings, stoppingToken);

                if (!await ProcessBatchAsync(settings.MaxParallelRuns, stoppingToken))
                {
                    _emptyPollStreak++;
                    var backoff = _emptyPollStreak switch
                    {
                        <= 1 => TimeSpan.FromSeconds(10),
                        2    => TimeSpan.FromSeconds(30),
                        _    => TimeSpan.FromSeconds(60),
                    };
                    await Task.Delay(backoff, stoppingToken);
                    continue;
                }

                _emptyPollStreak = 0;
                _healthMonitor?.RecordCycleSuccess(
                    nameof(WalkForwardWorker),
                    (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit without logging as an error.
                break;
            }
            catch (Exception ex)
            {
                // Swallow unexpected outer-loop exceptions so the worker stays alive
                // and continues retrying on the next tick.
                _healthMonitor?.RecordCycleFailure(nameof(WalkForwardWorker), ex.Message);
                _logger.LogError(ex, "Unexpected error in WalkForwardWorker polling loop");
            }

            await Task.Delay(BasePollInterval, stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(nameof(WalkForwardWorker));
        _logger.LogInformation("WalkForwardWorker stopped");
    }

    /// <summary>
    /// Runs a single polling cycle. Kept as a separate method for older tests that
    /// invoke one cycle directly via reflection.
    /// </summary>
    private async Task<bool> ProcessAsync(CancellationToken ct)
        => await ProcessBatchAsync(1, ct);

    private async Task RecoverExpiredRunsAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        await RecoverExpiredRunsAsync(settings, ct);
    }

    private async Task RecoverExpiredRunsAsync(WalkForwardWorkerSettings settings, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var nowUtc = UtcNow;

        var (requeued, orphaned) = await _runClaimService.RequeueExpiredRunsAsync(db, nowUtc, ct);
        var legacyCutoff = nowUtc.AddMinutes(-settings.StaleRunMinutes);
        var legacyRunningRuns = await db.Set<WalkForwardRun>()
            .Where(run => !run.IsDeleted
                       && run.Status == RunStatus.Running
                       && run.ExecutionLeaseExpiresAt == null
                       && (run.LastHeartbeatAt ?? run.ExecutionStartedAt ?? run.ClaimedAt ?? (DateTime?)run.StartedAt) < legacyCutoff)
            .ToListAsync(ct);

        foreach (var run in legacyRunningRuns)
            WalkForwardRunStateMachine.Transition(run, RunStatus.Queued, nowUtc);

        if (legacyRunningRuns.Count > 0)
            await writeContext.SaveChangesAsync(ct);

        int totalRecovered = requeued + orphaned + legacyRunningRuns.Count;
        if (totalRecovered > 0)
        {
            _healthMonitor?.RecordRecovery(nameof(WalkForwardWorker), totalRecovered);
            _logger.LogInformation(
                "WalkForwardWorker: recovered {Count} run(s) ({Requeued} re-queued, {Orphaned} orphaned, {Legacy} legacy)",
                totalRecovered,
                requeued,
                orphaned,
                legacyRunningRuns.Count);
        }
    }

    private async Task<bool> ProcessBatchAsync(int maxRuns, CancellationToken ct)
    {
        var claimedRunIds = await ClaimQueuedRunsAsync(maxRuns, ct);

        if (claimedRunIds.Count == 0)
            return false;

        _emptyPollStreak = 0;

        var tasks = claimedRunIds.Select(id => ProcessSingleRunAsync(id, ct));
        await Task.WhenAll(tasks);
        return true;
    }

    /// <summary>
    /// Claims up to <see cref="DefaultMaxParallelWalkForwards"/> queued walk-forward runs
    /// by atomically transitioning them from <see cref="RunStatus.Queued"/> to
    /// <see cref="RunStatus.Running"/>. Returns the list of claimed run IDs.
    /// </summary>
    private async Task<List<long>> ClaimQueuedRunsAsync(int maxRuns, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        int dueBacklogDepth = await db.Set<WalkForwardRun>()
            .CountAsync(run => !run.IsDeleted && run.Status == RunStatus.Queued && run.AvailableAt <= UtcNow, ct);
        _healthMonitor?.RecordBacklogDepth(nameof(WalkForwardWorker), dueBacklogDepth);
        var ids = new List<long>(Math.Max(1, maxRuns));
        for (int i = 0; i < Math.Max(1, maxRuns); i++)
        {
            var claimResult = await _runClaimService.ClaimNextRunAsync(db, UtcNow, _workerIdentity.InstanceId, ct);
            if (!claimResult.RunId.HasValue)
                break;
            ids.Add(claimResult.RunId.Value);
        }

        if (ids.Count == 0)
            return [];

        _logger.LogInformation("WalkForwardWorker: claimed {Count} run(s): [{Ids}]",
            ids.Count, string.Join(", ", ids));

        return ids;
    }

    /// <summary>
    /// Processes a single walk-forward run within its own DI scope. Loads the strategy
    /// and candle data, slides in-sample/out-of-sample windows, backtests each OOS window,
    /// aggregates results, and persists final metrics. Uses a double-completion guard to
    /// prevent concurrent workers from overwriting results.
    /// </summary>
    private async Task ProcessSingleRunAsync(long runId, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db = writeContext.GetDbContext();
            var settings = await _settingsProvider.GetWalkForwardSettingsAsync(db, _logger, ct);

            var run = await db.Set<WalkForwardRun>()
                .FirstOrDefaultAsync(candidate => candidate.Id == runId && !candidate.IsDeleted, ct);

            if (run == null)
            {
                _logger.LogWarning("WalkForwardWorker: run {RunId} disappeared after claiming", runId);
                return;
            }

            _logger.LogInformation(
                "WalkForwardWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);
            _healthMonitor?.RecordQueueLatency(
                nameof(WalkForwardWorker),
                (long)Math.Max(0, (UtcNow - run.QueuedAt).TotalMilliseconds));

            if (!run.ExecutionLeaseToken.HasValue)
            {
                _logger.LogWarning("WalkForwardWorker: run {RunId} has no execution lease token after claim", run.Id);
                return;
            }

            run.ExecutionStartedAt ??= UtcNow;
            run.LastAttemptAt ??= UtcNow;
            run.LastHeartbeatAt = run.ExecutionStartedAt;
            run.ExecutionLeaseExpiresAt = run.ExecutionStartedAt.Value.Add(WalkForwardExecutionLeasePolicy.LeaseDuration);
            run.ClaimedByWorkerId ??= _workerIdentity.InstanceId;

            try
            {
                await writeContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(
                    ex,
                    "WalkForwardWorker: lease ownership changed before run {RunId} could begin execution",
                    run.Id);
                return;
            }

            using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var leaseTask = WalkForwardRunLeaseMaintainer.MaintainExecutionLeaseAsync(
                _scopeFactory,
                _logger,
                run.Id,
                run.ExecutionLeaseToken.Value,
                leaseCts.Token);

            bool persisted = false;

            try
            {
                var baseStrategyForRun = await ResolveStrategyForExecutionAsync(db, run, ct);
                if (!string.IsNullOrWhiteSpace(run.ParametersSnapshotJson))
                    baseStrategyForRun.ParametersJson = run.ParametersSnapshotJson;

                if (string.IsNullOrWhiteSpace(run.BacktestOptionsSnapshotJson))
                {
                    run.BacktestOptionsSnapshotJson = JsonSerializer.Serialize(
                        await _optionsSnapshotBuilder.BuildAsync(db, run.Symbol, ct));
                }

                var optionsSnapshot = JsonSerializer.Deserialize<BacktestOptionsSnapshot>(run.BacktestOptionsSnapshotJson!)
                    ?? throw new JsonException("Walk-forward options snapshot could not be deserialized.");
                var backtestOptions = optionsSnapshot.ToOptions();

                if (run.InSampleDays <= 0 || run.OutOfSampleDays <= 0)
                {
                    throw new ValidationRunException(
                        ValidationRunFailureCodes.InvalidWindow,
                        $"Walk-forward window sizes must be positive (IS={run.InSampleDays}, OOS={run.OutOfSampleDays}).",
                        failureDetailsJson: ValidationRunException.SerializeDetails(new
                        {
                            run.Id,
                            run.InSampleDays,
                            run.OutOfSampleDays
                        }));
                }

                // Per-fold minimum day guards. Positive-only checks above don't prevent tiny
                // folds (e.g. 5-day IS / 2-day OOS) from producing unreliable CV stddev even
                // when the 3-window minimum is met. Both minimums are configurable via
                // WalkForward:MinInSampleDays / :MinOutOfSampleDays.
                if (run.InSampleDays < settings.MinInSampleDays)
                {
                    throw new ValidationRunException(
                        ValidationRunFailureCodes.InvalidWindow,
                        $"In-sample window of {run.InSampleDays} days is below the minimum of {settings.MinInSampleDays}.",
                        failureDetailsJson: ValidationRunException.SerializeDetails(new
                        {
                            run.Id,
                            run.InSampleDays,
                            settings.MinInSampleDays
                        }));
                }

                if (run.OutOfSampleDays < settings.MinOutOfSampleDays)
                {
                    throw new ValidationRunException(
                        ValidationRunFailureCodes.InvalidWindow,
                        $"Out-of-sample window of {run.OutOfSampleDays} days is below the minimum of {settings.MinOutOfSampleDays}.",
                        failureDetailsJson: ValidationRunException.SerializeDetails(new
                        {
                            run.Id,
                            run.OutOfSampleDays,
                            settings.MinOutOfSampleDays
                        }));
                }

                var allCandles = await db.Set<Candle>()
                    .Where(candle =>
                        candle.Symbol == run.Symbol &&
                        candle.Timeframe == run.Timeframe &&
                        candle.Timestamp >= run.FromDate &&
                        candle.Timestamp <= run.ToDate &&
                        candle.IsClosed &&
                        !candle.IsDeleted)
                    .OrderBy(candle => candle.Timestamp)
                    .Take(settings.MaxCandlesPerRun + 1)
                    .ToListAsync(ct);

                if (allCandles.Count == 0)
                {
                    throw new ValidationRunException(
                        ValidationRunFailureCodes.NoClosedCandles,
                        $"No closed candles found for {run.Symbol}/{run.Timeframe} between {run.FromDate:yyyy-MM-dd} and {run.ToDate:yyyy-MM-dd}.",
                        failureDetailsJson: ValidationRunException.SerializeDetails(new
                        {
                            run.Id,
                            run.Symbol,
                            run.Timeframe,
                            run.FromDate,
                            run.ToDate
                        }));
                }

                if (allCandles.Count > settings.MaxCandlesPerRun)
                {
                    throw new ValidationRunException(
                        ValidationRunFailureCodes.InvalidWindow,
                        $"Candle set exceeds the configured max size for validation ({settings.MaxCandlesPerRun}).",
                        failureDetailsJson: ValidationRunException.SerializeDetails(new
                        {
                            run.Id,
                            CandleCount = allCandles.Count,
                            settings.MaxCandlesPerRun
                        }));
                }

                var candleAssessment = await _candleSeriesGuard.ValidateAsync(
                    db,
                    run.Symbol,
                    run.Timeframe,
                    allCandles,
                    run.FromDate,
                    run.ToDate,
                    settings.CandleGapMultiplier,
                    ct);
                if (!candleAssessment.IsValid)
                {
                    throw new ValidationRunException(
                        ValidationRunFailureCodes.InvalidCandleSeries,
                        $"Invalid candle series: {candleAssessment.Issue}.",
                        failureDetailsJson: ValidationRunException.SerializeDetails(new
                        {
                            run.Id,
                            Issue = candleAssessment.Issue
                        }));
                }

                var windowResults = new List<WindowResult>();
                int windowIndex = 0;
                var windowStartUtc = run.FromDate;

                // Reserve the final 10% of the total date range as a pure holdout (terminal embargo).
                // No walk-forward window should include this data.
                DateTime embargoStart = run.FromDate.AddDays((run.ToDate - run.FromDate).TotalDays * 0.90);

                while (windowStartUtc < run.ToDate)
                {
                    DateTime inSampleStartUtc = windowStartUtc;
                    DateTime inSampleEndUtc = inSampleStartUtc.AddDays(run.InSampleDays);
                    DateTime oosStartUtc = inSampleEndUtc;
                    DateTime oosEndUtc = oosStartUtc.AddDays(run.OutOfSampleDays);
                    if (oosEndUtc > run.ToDate)
                        break;

                    // Stop before terminal embargo — the last 10% of data is held out
                    if (oosEndUtc > embargoStart)
                        break;

                    var inSampleCandles = SliceCandles(allCandles, inSampleStartUtc, inSampleEndUtc);
                    var oosCandles = SliceCandles(allCandles, oosStartUtc, oosEndUtc);

                    if (inSampleCandles.Count == 0 || oosCandles.Count == 0)
                    {
                        if (inSampleCandles.Count == 0)
                            _logger.LogWarning("Empty candle slice for IS window {Start}→{End}", inSampleStartUtc, inSampleEndUtc);
                        if (oosCandles.Count == 0)
                            _logger.LogWarning("Empty candle slice for OOS window {Start}→{End}", oosStartUtc, oosEndUtc);
                        windowStartUtc = windowStartUtc.AddDays(run.OutOfSampleDays);
                        continue;
                    }

                    // Per-fold candle floor: indicator warmup needs a minimum to produce a
                    // meaningful backtest. Below the threshold we reject the whole run rather
                    // than quietly skipping the fold and pretending the CV is based on 3+
                    // reliable scores.
                    if (inSampleCandles.Count < settings.MinCandlesPerFold ||
                        oosCandles.Count < settings.MinCandlesPerFold)
                    {
                        throw new ValidationRunException(
                            ValidationRunFailureCodes.InvalidWindow,
                            $"Walk-forward fold {windowIndex} has too few candles " +
                            $"(IS={inSampleCandles.Count}, OOS={oosCandles.Count}, min={settings.MinCandlesPerFold}).",
                            failureDetailsJson: ValidationRunException.SerializeDetails(new
                            {
                                run.Id,
                                WindowIndex = windowIndex,
                                InSampleCandles = inSampleCandles.Count,
                                OutOfSampleCandles = oosCandles.Count,
                                settings.MinCandlesPerFold
                            }));
                    }

                    Strategy evalStrategy = baseStrategyForRun;
                    if (run.ReOptimizePerFold
                        && string.IsNullOrWhiteSpace(run.ParametersSnapshotJson)
                        && inSampleCandles.Count >= 60)
                    {
                        var foldOptimised = await ReOptimizeOnFoldAsync(
                            baseStrategyForRun,
                            inSampleCandles,
                            run.InitialBalance,
                            backtestOptions,
                            ct);
                        if (foldOptimised is not null)
                        {
                            evalStrategy = foldOptimised;
                            _logger.LogDebug(
                                "WalkForwardWorker: run {RunId} window {Window} re-optimised params: {Params}",
                                run.Id, windowIndex, evalStrategy.ParametersJson);
                        }
                    }

                    var oosResult = await _backtestEngine.RunAsync(
                        evalStrategy,
                        oosCandles,
                        run.InitialBalance,
                        ct,
                        backtestOptions);

                    // Per-fold trade floor: a Sharpe computed on fewer than MinTradesPerFold
                    // trades has very wide confidence intervals and should not be averaged into
                    // the cross-fold CV. MinTradesPerFold = 0 disables this gate for backfill/
                    // smoke-test runs; the default (5) is a pragmatic floor that excludes
                    // degenerate folds without blocking genuinely low-turnover strategies.
                    if (settings.MinTradesPerFold > 0 && oosResult.TotalTrades < settings.MinTradesPerFold)
                    {
                        throw new ValidationRunException(
                            ValidationRunFailureCodes.InvalidWindow,
                            $"Walk-forward fold {windowIndex} produced {oosResult.TotalTrades} OOS trades " +
                            $"(min={settings.MinTradesPerFold}). Sharpe on this fold is unreliable.",
                            failureDetailsJson: ValidationRunException.SerializeDetails(new
                            {
                                run.Id,
                                WindowIndex = windowIndex,
                                OosTrades = oosResult.TotalTrades,
                                settings.MinTradesPerFold
                            }));
                    }

                    windowResults.Add(new WindowResult
                    {
                        WindowIndex = windowIndex,
                        InSampleFrom = inSampleCandles[0].Timestamp,
                        InSampleTo = inSampleCandles[^1].Timestamp,
                        OutOfSampleFrom = oosCandles[0].Timestamp,
                        OutOfSampleTo = oosCandles[^1].Timestamp,
                        OosHealthScore = (double)oosResult.SharpeRatio,
                        OosTotalTrades = oosResult.TotalTrades,
                        OosWinRate = (double)oosResult.WinRate,
                        OosProfitFactor = (double)oosResult.ProfitFactor,
                        UsedParametersJson = evalStrategy.ParametersJson
                    });

                    _logger.LogInformation(
                        "WalkForwardWorker: run {RunId} window {Window} OOS SharpeRatio={Sharpe:F4}",
                        run.Id, windowIndex, oosResult.SharpeRatio);

                    windowStartUtc = windowStartUtc.AddDays(run.OutOfSampleDays);
                    windowIndex++;
                }

                const int MinWindowsForReliableCV = 3;

                if (windowResults.Count < MinWindowsForReliableCV)
                {
                    _logger.LogWarning(
                        "Walk-forward produced only {Count} windows (minimum {Min} required for reliable CV)",
                        windowResults.Count, MinWindowsForReliableCV);

                    throw new ValidationRunException(
                        ValidationRunFailureCodes.InvalidWindow,
                        $"Insufficient walk-forward windows: {windowResults.Count} < {MinWindowsForReliableCV}",
                        failureDetailsJson: ValidationRunException.SerializeDetails(new
                        {
                            run.Id,
                            run.FromDate,
                            run.ToDate,
                            run.InSampleDays,
                            run.OutOfSampleDays,
                            WindowCount = windowResults.Count,
                            MinWindowsForReliableCV
                        }));
                }

                var scores = windowResults.Select(result => result.OosHealthScore).ToList();
                double avg = scores.Average();
                double sumSq = scores.Sum(score => Math.Pow(score - avg, 2));

                // Safety net: if fewer than MinWindowsForReliableCV windows, stddev is unreliable
                // — force quality gate failure. This should be caught above but guards edge cases.
                double stdDev;
                if (scores.Count < MinWindowsForReliableCV)
                    stdDev = double.MaxValue;
                else
                    stdDev = scores.Count > 1 ? Math.Sqrt(sumSq / Math.Max(1, scores.Count - 1)) : 0.0;

                run.AverageOutOfSampleScore = (decimal)avg;
                run.ScoreConsistency = (decimal)stdDev;
                run.WindowResultsJson = JsonSerializer.Serialize(windowResults);
                WalkForwardRunStateMachine.Transition(run, RunStatus.Completed, UtcNow);

                _logger.LogInformation(
                    "WalkForwardWorker: run {RunId} completed — Windows={Count}, AvgOOS={Avg:F4}, StdDev={Std:F4}",
                    run.Id, windowResults.Count, avg, stdDev);

                await writeContext.SaveChangesAsync(ct);
                persisted = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await RequeueCanceledRunAsync(writeContext, run);
                throw;
            }
            catch (Exception ex)
            {
                var failure = ValidationRetryPolicy.Classify(ex);
                var nowUtc = UtcNow;
                if (ValidationRetryPolicy.ShouldRetry(failure, run.RetryCount, settings.MaxRetryAttempts))
                {
                    var nextAvailableAtUtc = ValidationRetryPolicy.ComputeNextQueueTimeUtc(nowUtc, run.RetryCount, settings.RetryBackoffSeconds);
                    ValidationRetryPolicy.RequeueWalkForwardRunForRetry(run, nowUtc, nextAvailableAtUtc);
                    _healthMonitor?.RecordRetry(nameof(WalkForwardWorker));
                    _logger.LogWarning(
                        ex,
                        "WalkForwardWorker: run {RunId} hit transient failure {FailureCode}; re-queued for retry #{RetryCount} at {NextQueueTimeUtc:O}",
                        run.Id,
                        failure.FailureCode,
                        run.RetryCount,
                        nextAvailableAtUtc);
                }
                else
                {
                    _logger.LogError(ex, "WalkForwardWorker: run {RunId} failed", run.Id);
                    WalkForwardRunStateMachine.Transition(
                        run,
                        RunStatus.Failed,
                        nowUtc,
                        ex.Message,
                        failure.FailureCode,
                        failure.FailureDetailsJson);
                }
                try
                {
                    await writeContext.SaveChangesAsync(ct);
                    persisted = true;
                }
                catch (DbUpdateConcurrencyException concurrencyEx)
                {
                    _logger.LogWarning(
                        concurrencyEx,
                        "WalkForwardWorker: lease ownership changed before failure state for run {RunId} could be persisted",
                        run.Id);
                }
            }
            finally
            {
                leaseCts.Cancel();
                try
                {
                    await leaseTask;
                }
                catch (OperationCanceledException) when (leaseCts.IsCancellationRequested || ct.IsCancellationRequested)
                {
                }

                if (persisted && run.ExecutionStartedAt.HasValue && run.Status != RunStatus.Running)
                {
                    _healthMonitor?.RecordExecutionDuration(
                        nameof(WalkForwardWorker),
                        (long)Math.Max(0, (UtcNow - run.ExecutionStartedAt.Value).TotalMilliseconds));
                }
            }

            if (persisted
                && run.SourceOptimizationRunId.HasValue
                && (run.Status == RunStatus.Completed || run.Status == RunStatus.Failed))
            {
                try
                {
                    bool followUpPassed = run.Status == RunStatus.Completed;
                    if (followUpPassed)
                    {
                        // MaxCoefficientOfVariation is loaded from the canonical key
                    // "Optimization:MaxCvCoefficientOfVariation" via ValidationConfigReader — same
                    // key used by OptimizationConfigProvider to keep thresholds in sync.
                    if (!Optimization.OptimizationFollowUpQualityEvaluator.IsWalkForwardQualitySufficient(
                                run, settings.MaxCoefficientOfVariation, out string reason))
                        {
                            followUpPassed = false;
                            _logger.LogWarning(
                                "WalkForwardWorker: validation walk-forward for optimization run {OptimizationRunId} failed quality gate — {Reason}",
                                run.SourceOptimizationRunId.Value, reason);
                        }
                    }

                    await Optimization.OptimizationFollowUpTracker.UpdateStatusAsync(
                        db, run.SourceOptimizationRunId.Value,
                        followUpPassed, writeContext, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "WalkForwardWorker: failed to update optimization follow-up status for run {RunId} (non-fatal)",
                        run.SourceOptimizationRunId.Value);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WalkForwardWorker: unhandled error processing run {RunId}", runId);
        }
    }

    // ── Per-fold re-optimization ───────────────────────────────────────────────

    /// <summary>
    /// Runs a mini TPE search on the in-sample candles to find the best parameters for
    /// this fold. Returns a cloned strategy with optimised params, or null on failure.
    /// Uses a small budget (20 evaluations) to keep walk-forward runs tractable.
    /// </summary>
    private async Task<Strategy?> ReOptimizeOnFoldAsync(
        Strategy strategy, List<Candle> isCandles, decimal initialBalance, BacktestOptions backtestOptions, CancellationToken ct)
    {
        try
        {
            // Build parameter bounds from the strategy's current params as a baseline
            var currentParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(strategy.ParametersJson);
            if (currentParams is null || currentParams.Count == 0) return null;

            var bounds = new Dictionary<string, (double Min, double Max, bool IsInteger)>();
            foreach (var (key, val) in currentParams)
            {
                if (!val.TryGetDouble(out double baseVal)) continue;
                bool isInt = val.ValueKind == JsonValueKind.Number && !val.ToString()!.Contains('.');
                // Search ±50% around current value
                double margin = Math.Max(Math.Abs(baseVal) * 0.5, 1.0);
                bounds[key] = (baseVal - margin, baseVal + margin, isInt);
            }

            if (bounds.Count == 0) return null;

            var tpe = new TreeParzenEstimator(bounds, gamma: 0.25, seed: isCandles.Count);

            ScoredCandidate? best = null;
            const int budget = 20;

            // Seed with current params
            var baseResult = await _backtestEngine.RunAsync(strategy, isCandles, initialBalance, ct, backtestOptions);
            double baseScore = (double)ComputeHealthScore(baseResult);
            var baseDbl = currentParams.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.TryGetDouble(out double d) ? d : 0.0);
            tpe.AddObservation(baseDbl, baseScore);
            best = new ScoredCandidate(strategy.ParametersJson, (decimal)baseScore, baseResult);

            for (int i = 1; i < budget; i++)
            {
                ct.ThrowIfCancellationRequested();
                var suggestions = tpe.SuggestCandidates(1, minObservationsForModel: 5);
                if (suggestions.Count == 0) break;

                var suggestion = suggestions[0];
                var paramSet = suggestion.ToDictionary(
                    kv => kv.Key,
                    kv => bounds.TryGetValue(kv.Key, out var b) && b.IsInteger
                        ? (object)(int)Math.Round(kv.Value) : (object)kv.Value);
                var paramsJson = JsonSerializer.Serialize(paramSet);

                var candidate = CloneStrategy(strategy);
                candidate.ParametersJson = paramsJson;

                try
                {
                    var result = await _backtestEngine.RunAsync(candidate, isCandles, initialBalance, ct, backtestOptions);
                    decimal score = ComputeHealthScore(result);
                    tpe.AddObservation(suggestion, (double)score);

                    if (best is null || score > best.HealthScore)
                        best = new ScoredCandidate(paramsJson, score, result);
                }
                catch { /* skip failed candidate */ }
            }

            if (best is null) return null;

            var optimised = CloneStrategy(strategy);
            optimised.ParametersJson = best.ParamsJson;
            return optimised;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WalkForwardWorker: per-fold re-optimization failed — using fixed params");
            return null;
        }
    }

    private async Task RequeueCanceledRunAsync(IWriteApplicationDbContext writeContext, WalkForwardRun run)
    {
        try
        {
            WalkForwardRunStateMachine.Transition(run, RunStatus.Queued, UtcNow);
            await writeContext.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation(
                "WalkForwardWorker: re-queued run {RunId} after cancellation during shutdown",
                run.Id);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "WalkForwardWorker: lease ownership changed before canceled run {RunId} could be re-queued",
                run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "WalkForwardWorker: failed to re-queue canceled run {RunId}; lease recovery will handle it later",
                run.Id);
        }
    }

    private static Strategy CloneStrategy(Strategy source) => new()
    {
        Id                      = source.Id,
        Name                    = source.Name,
        Description             = source.Description,
        StrategyType            = source.StrategyType,
        Symbol                  = source.Symbol,
        Timeframe               = source.Timeframe,
        ParametersJson          = source.ParametersJson,
        Status                  = source.Status,
        RiskProfileId           = source.RiskProfileId,
        CreatedAt               = source.CreatedAt,
        LifecycleStage          = source.LifecycleStage,
        LifecycleStageEnteredAt = source.LifecycleStageEnteredAt,
        EstimatedCapacityLots   = source.EstimatedCapacityLots,
        IsDeleted               = source.IsDeleted
    };

    private static List<Candle> SliceCandles(
        List<Candle> candles,
        DateTime startUtc,
        DateTime endUtcExclusive)
    {
        int startIndex = LowerBound(candles, startUtc);
        int endIndex = LowerBound(candles, endUtcExclusive);
        int count = endIndex - startIndex;
        return count > 0 ? candles.GetRange(startIndex, count) : [];
    }

    private static int LowerBound(List<Candle> candles, DateTime timestamp)
    {
        int lo = 0;
        int hi = candles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (candles[mid].Timestamp < timestamp)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private async Task<Strategy> ResolveStrategyForExecutionAsync(
        DbContext db,
        WalkForwardRun run,
        CancellationToken ct)
    {
        var snapshot = _strategySnapshotBuilder.Deserialize(run.StrategySnapshotJson);
        if (snapshot is not null)
            return snapshot.ToStrategy();

        var strategy = await db.Set<Strategy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == run.StrategyId && !candidate.IsDeleted, ct);
        if (strategy == null)
        {
            throw new ValidationRunException(
                ValidationRunFailureCodes.StrategyNotFound,
                $"Strategy {run.StrategyId} not found.",
                failureDetailsJson: ValidationRunException.SerializeDetails(new
                {
                    run.Id,
                    run.StrategyId
                }));
        }

        run.StrategySnapshotJson = await _strategySnapshotBuilder.BuildSnapshotJsonAsync(
            db,
            run.StrategyId,
            run.ParametersSnapshotJson,
            ct);
        snapshot = _strategySnapshotBuilder.Deserialize(run.StrategySnapshotJson);
        if (snapshot is null)
        {
            throw new ValidationRunException(
                ValidationRunFailureCodes.InvalidStrategySnapshot,
                $"Strategy snapshot for walk-forward run {run.Id} could not be built or deserialized.",
                failureDetailsJson: ValidationRunException.SerializeDetails(new
                {
                    run.Id,
                    run.StrategyId
                }));
        }

        return snapshot.ToStrategy();
    }

    /// <summary>
    /// 5-factor health score aligned with OptimizationWorker.ComputeHealthScore.
    /// </summary>
    private static decimal ComputeHealthScore(BacktestResult r)
    {
        return 0.25m * r.WinRate
             + 0.20m * Math.Min(1m, r.ProfitFactor / 2m)
             + 0.20m * Math.Max(0m, 1m - r.MaxDrawdownPct / 20m)
             + 0.15m * Math.Min(1m, Math.Max(0m, r.SharpeRatio) / 2m)
             + 0.20m * Math.Min(1m, r.TotalTrades / 50m);
    }

    private sealed record ScoredCandidate(string ParamsJson, decimal HealthScore, BacktestResult Result);

    private async Task<WalkForwardWorkerSettings> LoadSettingsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        return await _settingsProvider.GetWalkForwardSettingsAsync(writeContext.GetDbContext(), _logger, ct);
    }

    // ── Window result record ───────────────────────────────────────────────────

    /// <summary>
    /// Immutable snapshot of the performance metrics observed during a single
    /// out-of-sample window in the walk-forward analysis. One instance is produced
    /// per sliding window and the full collection is serialised to
    /// <see cref="WalkForwardRun.WindowResultsJson"/> for later API retrieval.
    /// </summary>
    private sealed record WindowResult
    {
        /// <summary>Zero-based index of this window within the walk-forward sequence.</summary>
        public int      WindowIndex         { get; init; }

        /// <summary>UTC timestamp of the first candle in the in-sample segment.</summary>
        public DateTime InSampleFrom        { get; init; }

        /// <summary>UTC timestamp of the last candle in the in-sample segment (inclusive).</summary>
        public DateTime InSampleTo          { get; init; }

        /// <summary>UTC timestamp of the first candle in the out-of-sample segment.</summary>
        public DateTime OutOfSampleFrom     { get; init; }

        /// <summary>UTC timestamp of the last candle in the out-of-sample segment (inclusive).</summary>
        public DateTime OutOfSampleTo       { get; init; }

        /// <summary>
        /// Primary OOS quality metric — the annualised Sharpe ratio produced by the
        /// backtest engine over the out-of-sample candle slice. Used to compute
        /// <see cref="WalkForwardRun.AverageOutOfSampleScore"/> and
        /// <see cref="WalkForwardRun.ScoreConsistency"/>.
        /// </summary>
        public double   OosHealthScore      { get; init; }

        /// <summary>Total number of trades entered during the OOS evaluation period.</summary>
        public int      OosTotalTrades      { get; init; }

        /// <summary>Fraction of OOS trades that were profitable, in [0, 1].</summary>
        public double   OosWinRate          { get; init; }

        /// <summary>Gross OOS profit divided by gross OOS loss (profit factor).</summary>
        public double   OosProfitFactor     { get; init; }

        /// <summary>
        /// JSON of the parameters used for this window's OOS evaluation. When
        /// <see cref="WalkForwardRun.ReOptimizePerFold"/> is true, this contains the
        /// per-fold optimised parameters; otherwise it is the strategy's fixed params.
        /// </summary>
        public string?  UsedParametersJson  { get; init; }
    }
}
