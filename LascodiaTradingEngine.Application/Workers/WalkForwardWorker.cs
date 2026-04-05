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

    /// <summary>
    /// How long the worker sleeps between polling cycles. 30 seconds balances
    /// responsiveness against the CPU cost of multi-window backtest evaluations.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for diagnostic output.</param>
    /// <param name="scopeFactory">Factory for creating per-cycle DI scopes.</param>
    /// <param name="backtestEngine">Engine used to evaluate each OOS window.</param>
    public WalkForwardWorker(
        ILogger<WalkForwardWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
    }

    /// <summary>
    /// Entry point invoked by the hosted-service runtime. Runs a continuous polling
    /// loop that delegates each tick to <see cref="ProcessAsync"/> and waits
    /// <see cref="PollingInterval"/> between iterations.
    /// </summary>
    /// <param name="stoppingToken">
    /// Signalled by the runtime on application shutdown, causing the loop to exit
    /// gracefully once the current processing cycle completes.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WalkForwardWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAsync(stoppingToken);
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

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("WalkForwardWorker stopped");
    }

    /// <summary>
    /// Core processing method for a single polling tick. Dequeues the oldest
    /// <see cref="RunStatus.Queued"/> walk-forward run, slides in-sample/out-of-sample
    /// windows across the full candle dataset, backtests the strategy on each OOS window,
    /// aggregates the results, and persists the final metrics. Returns immediately (no-op)
    /// when the queue is empty.
    /// </summary>
    /// <remarks>
    /// A fresh DI scope is created on every call to ensure EF Core DbContext instances are
    /// isolated and disposed promptly, preventing change-tracker bloat over long-running runs
    /// with many candle rows loaded.
    /// </remarks>
    /// <param name="ct">Cancellation token propagated from <see cref="ExecuteAsync"/>.</param>
    private async Task ProcessAsync(CancellationToken ct)
    {
        // Fresh DI scope per processing cycle for proper EF context isolation.
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var db = writeContext.GetDbContext();

        // Pick the oldest queued walk-forward run (FIFO by StartedAt).
        var run = await db.Set<WalkForwardRun>()
            .Where(r => r.Status == RunStatus.Queued && !r.IsDeleted)
            .OrderBy(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        // Nothing in the queue — sleep until the next polling tick.
        if (run == null) return;

        _logger.LogInformation(
            "WalkForwardWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

        // Load strategy via the read-side context (CQRS separation).
        // If the strategy has been deleted since the run was queued, fail fast
        // rather than running a meaningless analysis.
        var strategy = await readContext.GetDbContext()
            .Set<Strategy>()
            .FirstOrDefaultAsync(s => s.Id == run.StrategyId && !s.IsDeleted, ct);

        if (strategy == null)
        {
            // Fail the run immediately; no point claiming it as Running first because
            // there is no work to do — the strategy no longer exists.
            run.Status       = RunStatus.Failed;
            run.ErrorMessage = $"Strategy {run.StrategyId} not found.";
            run.CompletedAt  = DateTime.UtcNow;
            await writeContext.SaveChangesAsync(ct);
            return;
        }

        // Claim the run by setting it to Running so no concurrent worker picks it up.
        run.Status = RunStatus.Running;
        await writeContext.SaveChangesAsync(ct);

        var baseStrategyForRun = CloneStrategy(strategy);
        if (!string.IsNullOrWhiteSpace(run.ParametersSnapshotJson))
            baseStrategyForRun.ParametersJson = run.ParametersSnapshotJson;

        try
        {
            // Load all closed candles spanning the full date window in chronological order.
            // The entire dataset is loaded into memory because the window-sliding loop
            // accesses arbitrary sub-ranges via Skip/Take — streaming would require
            // repeated round-trips that cost more than a single bulk load.
            var allCandles = await readContext.GetDbContext()
                .Set<Candle>()
                .Where(c =>
                    c.Symbol    == run.Symbol    &&
                    c.Timeframe == run.Timeframe &&
                    c.Timestamp >= run.FromDate  &&
                    c.Timestamp <= run.ToDate    &&
                    c.IsClosed                   &&  // Exclude the in-progress (open) bar
                    !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (allCandles.Count == 0)
                throw new InvalidOperationException(
                    $"No closed candles found for {run.Symbol}/{run.Timeframe} between {run.FromDate:yyyy-MM-dd} and {run.ToDate:yyyy-MM-dd}.");

            // Total bars required to fit at least one complete window.
            int windowSize    = run.InSampleDays + run.OutOfSampleDays;
            var windowResults = new List<WindowResult>();

            int windowIndex = 0;
            int offset      = 0;  // Start index of the current window's IS segment in allCandles

            // ── Walk-forward loop ──────────────────────────────────────────────────
            // Each iteration advances the window by OutOfSampleDays bars (anchored
            // walk-forward). This means:
            //   - The IS segment grows by one OOS period each iteration (anchored IS start)
            //     — OR —
            //   - The IS segment slides forward maintaining a fixed length (rolling IS).
            // Here the implementation uses fixed-length IS/OOS slices via Skip/Take,
            // so both segments maintain constant length and the window slides forward
            // by OutOfSampleDays bars.
            while (offset + windowSize <= allCandles.Count)
            {
                // ── Index arithmetic for this window ─────────────────────────────
                int inSampleStart  = offset;
                int inSampleEnd    = offset + run.InSampleDays;   // Exclusive upper bound
                int oosStart       = inSampleEnd;                  // OOS starts immediately after IS
                int oosEnd         = oosStart + run.OutOfSampleDays; // Exclusive upper bound

                // Guard: ensure the OOS segment is fully contained in allCandles.
                // This can fail on the last iteration if the remaining data is less than
                // a full OOS block.
                if (oosEnd > allCandles.Count) break;

                var inSampleCandles = allCandles
                    .Skip(inSampleStart)
                    .Take(run.InSampleDays)
                    .ToList();

                var oosCandles = allCandles
                    .Skip(oosStart)
                    .Take(run.OutOfSampleDays)
                    .ToList();

                // ── Per-fold re-optimization (true WFA) ─────────────────────
                // When enabled, re-optimise strategy parameters on the IS candles using a
                // mini TPE search before evaluating on the OOS segment. This validates
                // whether the optimisation process itself consistently finds good params.
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

                // Record full per-window metrics for the JSON breakdown stored in WindowResultsJson.
                // OosHealthScore maps to SharpeRatio so the AverageOutOfSampleScore field
                // carries a risk-adjusted return estimate rather than a raw profit figure.
                var windowResult = new WindowResult
                {
                    WindowIndex         = windowIndex,
                    InSampleFrom        = allCandles[inSampleStart].Timestamp,
                    InSampleTo          = allCandles[inSampleEnd - 1].Timestamp,   // Last inclusive bar
                    OutOfSampleFrom     = allCandles[oosStart].Timestamp,
                    OutOfSampleTo       = allCandles[oosEnd - 1].Timestamp,         // Last inclusive bar
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

                // Advance the window start by one OOS period so the next window's IS
                // segment begins where this window's OOS segment started.
                offset      += run.OutOfSampleDays;
                windowIndex++;
            }

            if (windowResults.Count == 0)
                throw new InvalidOperationException("Not enough candle data to form any walk-forward windows.");

            // ── Aggregate statistics across all OOS windows ────────────────────────
            // Mean Sharpe: primary quality signal — higher is better.
            // Std-dev of Sharpe: consistency signal — lower means the strategy performs
            //   similarly across different market conditions rather than excelling in one
            //   regime and failing in another (a sign of robustness).
            var scores = windowResults.Select(w => w.OosHealthScore).ToList();
            double avg    = scores.Average();
            double mean   = avg;  // Alias for variance calculation below
            double sumSq  = scores.Sum(s => Math.Pow(s - mean, 2));
            // Population std-dev (divide by N, not N-1) is used here because we want to
            // describe the observed dispersion across ALL windows, not estimate a larger
            // population's variance from a sample.
            double stdDev = scores.Count > 1 ? Math.Sqrt(sumSq / scores.Count) : 0.0;

            run.AverageOutOfSampleScore = (decimal)avg;
            run.ScoreConsistency        = (decimal)stdDev;
            // Persist the full per-window breakdown so the API can expose detailed analytics.
            run.WindowResultsJson       = JsonSerializer.Serialize(windowResults);
            run.Status                  = RunStatus.Completed;
            run.CompletedAt             = DateTime.UtcNow;

            _logger.LogInformation(
                "WalkForwardWorker: run {RunId} completed — Windows={Count}, AvgOOS={Avg:F4}, StdDev={Std:F4}",
                run.Id, windowResults.Count, avg, stdDev);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WalkForwardWorker: run {RunId} failed", run.Id);
            run.Status       = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            run.CompletedAt  = DateTime.UtcNow;
        }

        // Single SaveChanges call persists the final status, all metrics, and the
        // JSON breakdown in one database round-trip.
        await writeContext.SaveChangesAsync(ct);

        // ── Update optimization follow-up status if this was a validation walk-forward ──
        if (run.SourceOptimizationRunId.HasValue)
        {
            try
            {
                bool followUpPassed = run.Status == RunStatus.Completed;
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
