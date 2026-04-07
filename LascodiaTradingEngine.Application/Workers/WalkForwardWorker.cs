using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
///   <item><term>ScoreConsistency</term><description>Population standard deviation of Sharpe scores — lower is more consistent.</description></item>
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
        IWorkerHealthMonitor? healthMonitor = null)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
        _healthMonitor  = healthMonitor;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessBatchAsync(DefaultMaxParallelWalkForwards, stoppingToken))
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
                _logger.LogError(ex, "Unexpected error in WalkForwardWorker polling loop");
            }

            await Task.Delay(BasePollInterval, stoppingToken);
        }

        _logger.LogInformation("WalkForwardWorker stopped");
    }

    /// <summary>
    /// Runs a single polling cycle. Kept as a separate method for older tests that
    /// invoke one cycle directly via reflection.
    /// </summary>
    private async Task<bool> ProcessAsync(CancellationToken ct)
        => await ProcessBatchAsync(1, ct);

    private async Task<bool> ProcessBatchAsync(int maxRuns, CancellationToken ct)
    {
        var claimedRunIds = await ClaimQueuedRunsAsync(maxRuns, ct);
        _healthMonitor?.RecordBacklogDepth(nameof(WalkForwardWorker), claimedRunIds.Count);

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

        var runs = await db.Set<WalkForwardRun>()
            .Where(r => r.Status == RunStatus.Queued && !r.IsDeleted)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.StartedAt)
            .Take(Math.Max(1, maxRuns))
            .ToListAsync(ct);

        if (runs.Count == 0) return [];

        foreach (var run in runs)
            run.Status = RunStatus.Running;

        await writeContext.SaveChangesAsync(ct);

        var ids = runs.Select(r => r.Id).ToList();
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
            var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var db = writeContext.GetDbContext();

            var run = await db.Set<WalkForwardRun>()
                .FirstOrDefaultAsync(r => r.Id == runId && !r.IsDeleted, ct);

            if (run == null)
            {
                _logger.LogWarning("WalkForwardWorker: run {RunId} disappeared after claiming", runId);
                return;
            }

            _logger.LogInformation(
                "WalkForwardWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

            // Load strategy via the read-side context (CQRS separation).
            var strategy = await readContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(s => s.Id == run.StrategyId && !s.IsDeleted, ct);

            if (strategy == null)
            {
                run.Status       = RunStatus.Failed;
                run.ErrorMessage = $"Strategy {run.StrategyId} not found.";
                run.CompletedAt  = DateTime.UtcNow;
                await writeContext.SaveChangesAsync(ct);
                return;
            }

            var baseStrategyForRun = CloneStrategy(strategy);
            if (!string.IsNullOrWhiteSpace(run.ParametersSnapshotJson))
                baseStrategyForRun.ParametersJson = run.ParametersSnapshotJson;

            RunStatus finalStatus;
            string? errorMessage = null;

            try
            {
                var allCandles = await readContext.GetDbContext()
                    .Set<Candle>()
                    .Where(c =>
                        c.Symbol    == run.Symbol    &&
                        c.Timeframe == run.Timeframe &&
                        c.Timestamp >= run.FromDate  &&
                        c.Timestamp <= run.ToDate    &&
                        c.IsClosed                   &&
                        !c.IsDeleted)
                    .OrderBy(c => c.Timestamp)
                    .ToListAsync(ct);

                if (allCandles.Count == 0)
                    throw new InvalidOperationException(
                        $"No closed candles found for {run.Symbol}/{run.Timeframe} between {run.FromDate:yyyy-MM-dd} and {run.ToDate:yyyy-MM-dd}.");

                var windowResults = new List<WindowResult>();

                int windowIndex = 0;
                var windowStartUtc = run.FromDate;

                while (windowStartUtc < run.ToDate)
                {
                    DateTime inSampleStartUtc = windowStartUtc;
                    DateTime inSampleEndUtc = inSampleStartUtc.AddDays(run.InSampleDays);
                    DateTime oosStartUtc = inSampleEndUtc;
                    DateTime oosEndUtc = oosStartUtc.AddDays(run.OutOfSampleDays);
                    if (oosEndUtc > run.ToDate) break;

                    var inSampleCandles = allCandles
                        .Where(c => c.Timestamp >= inSampleStartUtc && c.Timestamp < inSampleEndUtc)
                        .ToList();

                    var oosCandles = allCandles
                        .Where(c => c.Timestamp >= oosStartUtc && c.Timestamp < oosEndUtc)
                        .ToList();

                    if (inSampleCandles.Count == 0 || oosCandles.Count == 0)
                    {
                        windowStartUtc = windowStartUtc.AddDays(run.OutOfSampleDays);
                        continue;
                    }

                    Strategy evalStrategy = baseStrategyForRun;
                    if (run.ReOptimizePerFold
                        && string.IsNullOrWhiteSpace(run.ParametersSnapshotJson)
                        && inSampleCandles.Count >= 60)
                    {
                        var foldOptimised = await ReOptimizeOnFoldAsync(baseStrategyForRun, inSampleCandles, run.InitialBalance, ct);
                        if (foldOptimised is not null)
                        {
                            evalStrategy = foldOptimised;
                            _logger.LogDebug(
                                "WalkForwardWorker: run {RunId} window {Window} re-optimised params: {Params}",
                                run.Id, windowIndex, evalStrategy.ParametersJson);
                        }
                    }

                    var oosResult = await _backtestEngine.RunAsync(evalStrategy, oosCandles, run.InitialBalance, ct);

                    var windowResult = new WindowResult
                    {
                        WindowIndex         = windowIndex,
                        InSampleFrom        = inSampleCandles[0].Timestamp,
                        InSampleTo          = inSampleCandles[^1].Timestamp,
                        OutOfSampleFrom     = oosCandles[0].Timestamp,
                        OutOfSampleTo       = oosCandles[^1].Timestamp,
                        OosHealthScore      = (double)oosResult.SharpeRatio,
                        OosTotalTrades      = oosResult.TotalTrades,
                        OosWinRate          = (double)oosResult.WinRate,
                        OosProfitFactor     = (double)oosResult.ProfitFactor,
                        UsedParametersJson  = evalStrategy.ParametersJson
                    };

                    windowResults.Add(windowResult);

                    _logger.LogInformation(
                        "WalkForwardWorker: run {RunId} window {Window} OOS SharpeRatio={Sharpe:F4}",
                        run.Id, windowIndex, oosResult.SharpeRatio);

                    windowStartUtc = windowStartUtc.AddDays(run.OutOfSampleDays);
                    windowIndex++;
                }

                if (windowResults.Count == 0)
                    throw new InvalidOperationException("Not enough candle data to form any walk-forward windows.");

                var scores = windowResults.Select(w => w.OosHealthScore).ToList();
                double avg    = scores.Average();
                double mean   = avg;
                double sumSq  = scores.Sum(s => Math.Pow(s - mean, 2));
                double stdDev = scores.Count > 1 ? Math.Sqrt(sumSq / scores.Count) : 0.0;

                run.AverageOutOfSampleScore = (decimal)avg;
                run.ScoreConsistency        = (decimal)stdDev;
                run.WindowResultsJson       = JsonSerializer.Serialize(windowResults);
                finalStatus                 = RunStatus.Completed;
                run.CompletedAt             = DateTime.UtcNow;

                _logger.LogInformation(
                    "WalkForwardWorker: run {RunId} completed — Windows={Count}, AvgOOS={Avg:F4}, StdDev={Std:F4}",
                    run.Id, windowResults.Count, avg, stdDev);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WalkForwardWorker: run {RunId} failed", run.Id);
                finalStatus  = RunStatus.Failed;
                errorMessage = ex.Message;
                run.CompletedAt = DateTime.UtcNow;
            }

            // ── Double-completion guard ─────────────────────────────────────────────
            // Use ExecuteUpdateAsync with a WHERE clause requiring Status == Running to
            // prevent concurrent workers from overwriting results if a run was somehow
            // claimed by multiple instances.
            int updatedRows;
            if (finalStatus == RunStatus.Completed)
            {
                updatedRows = await db.Set<WalkForwardRun>()
                    .Where(r => r.Id == runId && r.Status == RunStatus.Running)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, RunStatus.Completed)
                        .SetProperty(r => r.CompletedAt, run.CompletedAt)
                        .SetProperty(r => r.AverageOutOfSampleScore, run.AverageOutOfSampleScore)
                        .SetProperty(r => r.ScoreConsistency, run.ScoreConsistency)
                        .SetProperty(r => r.WindowResultsJson, run.WindowResultsJson),
                    ct);
            }
            else
            {
                updatedRows = await db.Set<WalkForwardRun>()
                    .Where(r => r.Id == runId && r.Status == RunStatus.Running)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, RunStatus.Failed)
                        .SetProperty(r => r.CompletedAt, run.CompletedAt)
                        .SetProperty(r => r.ErrorMessage, errorMessage),
                    ct);
            }

            if (updatedRows == 0)
            {
                _logger.LogWarning(
                    "WalkForwardWorker: double-completion guard — run {RunId} was no longer Running, skipping result persist",
                    runId);
                return;
            }

            // ── Update optimization follow-up status if this was a validation walk-forward ──
            if (run.SourceOptimizationRunId.HasValue)
            {
                try
                {
                    bool followUpPassed = finalStatus == RunStatus.Completed;
                    if (followUpPassed)
                    {
                        decimal maxCv = await GetConfigAsync(db, "Optimization:MaxCvCoefficientOfVariation", 0.50m, ct);
                        if (!Optimization.OptimizationFollowUpQualityEvaluator.IsWalkForwardQualitySufficient(
                                run, maxCv, out string reason))
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
            // Graceful shutdown — let Task.WhenAll propagate.
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
        Strategy strategy, List<Candle> isCandles, decimal initialBalance, CancellationToken ct)
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
            var baseResult = await _backtestEngine.RunAsync(strategy, isCandles, initialBalance, ct);
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
                    var result = await _backtestEngine.RunAsync(candidate, isCandles, initialBalance, ct);
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

    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    private sealed record ScoredCandidate(string ParamsJson, decimal HealthScore, BacktestResult Result);

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
