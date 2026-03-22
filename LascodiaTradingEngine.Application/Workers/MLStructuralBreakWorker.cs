using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Applies the Bai-Perron sequential structural break test to each active ML model's
/// prediction-outcome series. When a break is confirmed at the 99% significance level,
/// the model is immediately suppressed and an emergency retraining run is queued.
///
/// <para>
/// <b>What structural break detection means in this context:</b><br/>
/// A "structural break" in the prediction-outcome series means the model's average accuracy
/// has shifted abruptly at a specific point in time — not gradually (as concept drift would)
/// but suddenly, as if the underlying data-generating process has changed regime. Examples
/// of events that cause structural breaks in FX models:
/// <list type="bullet">
///   <item>Central-bank policy pivots (e.g., surprise rate decisions, quantitative easing/tightening).</item>
///   <item>Market microstructure changes (e.g., new exchange regulations, liquidity crises).</item>
///   <item>Geopolitical shocks that permanently change currency volatility regimes.</item>
///   <item>Data-pipeline faults that alter the feature distribution in a step-change fashion.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Algorithm — Bai-Perron Sup-F test (single break):</b><br/>
/// The test evaluates the null hypothesis H₀ that the mean of the outcome series is constant
/// against the alternative H₁ that there is a single break at some unknown position τ.
///
/// For each candidate break position τ ∈ [h·n, (1-h)·n] (interior trimmed region):
/// <code>
///   F(τ) = ((SS_all − SS_break) / q) / (SS_break / (n − 2))
/// </code>
/// Where:
/// <list type="bullet">
///   <item><c>SS_all</c> — total sum of squared deviations from the overall mean (unrestricted model).</item>
///   <item><c>SS_break = SS_1(τ) + SS_2(τ)</c> — sum of squared deviations from segment means (restricted model).</item>
///   <item><c>q = 1</c> — number of restrictions (one break, one additional mean parameter).</item>
///   <item><c>n</c> — total number of observations.</item>
/// </list>
/// The test statistic is the <em>supremum</em> of F(τ) over all interior τ:
/// <code>
///   SupF = max_τ F(τ)
/// </code>
/// If SupF &gt; <see cref="SupFCritical99"/> (12.16 at 99% confidence for q=1, h=0.15),
/// the null hypothesis is rejected and a structural break is declared.
/// </para>
///
/// <para>
/// <b>Critical value reference:</b><br/>
/// The critical value 12.16 is taken from Andrews (1993) Table 1, for q=1 (single structural
/// parameter), α=0.01 (99% confidence), and trimming fraction h=0.15. This is the standard
/// reference for the Sup-F distribution, which is non-standard (not chi-squared or F) and
/// must be tabulated rather than computed analytically.
/// </para>
///
/// <para>
/// <b>Response on detection:</b><br/>
/// <list type="number">
///   <item>The model is immediately suppressed (<c>IsSuppressed = true</c>) to prevent it from
///       generating trading signals while degraded.</item>
///   <item>An emergency <see cref="MLTrainingRun"/> (<c>IsEmergencyRetrain = true</c>) is queued,
///       bypassing the normal retrain priority queue. The <see cref="MLTrainingWorker"/> picks
///       this up and executes it ahead of regular scheduled retrains.</item>
/// </list>
/// This is the most aggressive response in the drift detection pipeline — justified because a
/// confirmed structural break means the model's predictions are drawn from a fundamentally
/// different (and now stale) distribution.
/// </para>
///
/// <para>
/// <b>Differences from other drift detectors:</b>
/// <list type="table">
///   <listheader><term>Worker</term><description>What it detects / how</description></listheader>
///   <item><term><see cref="MLCusumDriftWorker"/></term><description>Sequential CUSUM; detects step changes in accuracy using a running statistic. Does not identify the exact break date.</description></item>
///   <item><term><see cref="MLAdwinDriftWorker"/></term><description>Adaptive windowing; self-adjusting window, no prior on drift timing.</description></item>
///   <item><term><see cref="MLPeltChangePointWorker"/></term><description>Multiple change points on raw price returns; proactive (does not require accuracy degradation to fire).</description></item>
///   <item><term>MLStructuralBreakWorker (this)</term><description>Formal statistical test with known significance level; directly tests whether the accuracy distribution has changed at a specific unknown date. Triggers emergency suppression.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Scheduling:</b><br/>
/// Runs weekly (every 7 days) with a 20-minute initial delay to allow the application to
/// fully start up and seed/migrate the database before the first cycle. The weekly cadence
/// is appropriate because structural breaks are by definition rare, high-impact events;
/// running more frequently would not yield additional detections while consuming resources.
/// </para>
/// </summary>
public class MLStructuralBreakWorker : BackgroundService
{
    private readonly ILogger<MLStructuralBreakWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan _interval     = TimeSpan.FromDays(7);
    private static readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(20);

    /// <summary>Minimum resolved predictions required to run the test.</summary>
    private const int MinObservations = 60;

    /// <summary>Number of days of resolved outcomes to test (rolling window).</summary>
    private const int WindowDays = 90;

    /// <summary>
    /// Sup-F 99% critical value for a single break with trimming parameter h=0.15.
    /// From Andrews (1993) Table 1, q=1 (single variable), α=0.01: ~12.16.
    /// </summary>
    private const double SupFCritical99 = 12.16;

    /// <summary>
    /// Trimming parameter: breakpoints are only tested in the interior [h, 1-h]
    /// to ensure enough observations on each side. Default 15%.
    /// </summary>
    private const double TrimFraction = 0.15;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for diagnostic, warning, and error output.</param>
    /// <param name="scopeFactory">
    /// DI scope factory. A fresh scope is created per weekly run cycle to ensure EF Core
    /// contexts are properly disposed and do not hold stale state between cycles.
    /// </param>
    public MLStructuralBreakWorker(
        ILogger<MLStructuralBreakWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure on application start.
    /// Waits for a short initial delay, then runs the Bai-Perron structural break test cycle
    /// weekly until the application shuts down.
    /// </summary>
    /// <remarks>
    /// The 20-minute initial delay (<see cref="_initialDelay"/>) allows the application to
    /// complete startup (database migrations, DI wiring, event-bus connection) before the
    /// first heavy database query is issued, reducing startup contention.
    /// </remarks>
    /// <param name="stoppingToken">Graceful-shutdown token from the .NET host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLStructuralBreakWorker starting");

        // Wait for application warm-up before the first cycle.
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; } // Clean shutdown
            catch (Exception ex) { _logger.LogError(ex, "MLStructuralBreakWorker cycle failed"); }

            await Task.Delay(_interval, stoppingToken); // Wait 7 days before the next cycle
        }
    }

    /// <summary>
    /// Executes one full Bai-Perron test cycle: loads all active, non-suppressed models and
    /// runs the structural break test on each one.
    /// </summary>
    /// <remarks>
    /// Already-suppressed models are excluded because they are awaiting retraining and cannot
    /// generate new prediction outcomes; applying the break test to them would use stale data.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb       = readCtx.GetDbContext();
        var writeDb      = writeCtx.GetDbContext();

        // Only test active, non-suppressed models. Suppressed models are already flagged for retrain.
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsSuppressed && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            await TestModelAsync(model, readDb, writeDb, ct);
        }
    }

    /// <summary>
    /// Applies the Bai-Perron Sup-F test to a single model's prediction-outcome series.
    /// If the Sup-F statistic exceeds the 99% critical value, suppresses the model and
    /// queues an emergency retraining run.
    /// </summary>
    /// <remarks>
    /// The outcomes series is treated as a Bernoulli sequence y_t ∈ {0, 1} where y_t = 1
    /// if the model's prediction at time t was correct and y_t = 0 if it was incorrect.
    /// The Bai-Perron test then asks: is there a single point in time τ at which the
    /// expected correctness probability π changed abruptly?
    /// </remarks>
    /// <param name="model">The active ML model under test.</param>
    /// <param name="readDb">Read-side EF Core context for loading prediction logs.</param>
    /// <param name="writeDb">Write-side EF Core context for suppressing the model and creating training runs.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task TestModelAsync(
        MLModel model,
        Microsoft.EntityFrameworkCore.DbContext readDb,
        Microsoft.EntityFrameworkCore.DbContext writeDb,
        CancellationToken ct)
    {
        // Load the rolling 90-day window of resolved prediction outcomes.
        // Outcomes are projected to doubles (1.0/0.0) for numerical processing.
        var cutoff = DateTime.UtcNow.AddDays(-WindowDays);

        var outcomes = await readDb.Set<MLModelPredictionLog>()
            .Where(p => p.MLModelId == model.Id
                     && p.DirectionCorrect != null
                     && p.PredictedAt >= cutoff
                     && !p.IsDeleted)
            .OrderBy(p => p.PredictedAt)  // Chronological order is required for the break-point scan
            .Select(p => p.DirectionCorrect!.Value ? 1.0 : 0.0)
            .ToListAsync(ct);

        // Enforce minimum sample size. The Sup-F test with h=0.15 trimming requires at least
        // (2 × TrimFraction × n) = 2 × 0.15 × n = 0.30n observations per sub-segment,
        // which needs n ≥ MinObservations = 60 to guarantee at least 9 per side.
        if (outcomes.Count < MinObservations)
        {
            _logger.LogDebug("Model {Id}: insufficient observations ({N}) for Bai-Perron test", model.Id, outcomes.Count);
            return;
        }

        // ── Compute the Sup-F test statistic ──────────────────────────────────
        double supF = ComputeSupF(outcomes);

        _logger.LogDebug("Model {Id} Sup-F = {SupF:F4} (threshold {Threshold})", model.Id, supF, SupFCritical99);

        // ── Compare against the 99% critical value ────────────────────────────
        // If SupF < 12.16, the null hypothesis (no break) is not rejected.
        // There is insufficient statistical evidence to declare a structural break.
        if (supF < SupFCritical99) return;

        // ── Structural break confirmed at 99% confidence ───────────────────────
        _logger.LogWarning(
            "Structural break detected for model {Id} ({Symbol}/{Timeframe}) — Sup-F={SupF:F2} > {Critical}. Suppressing and queuing emergency retrain.",
            model.Id, model.Symbol, model.Timeframe, supF, SupFCritical99);

        // ── Suppress the model ─────────────────────────────────────────────────
        // IsSuppressed = true prevents the model from generating live trading signals while
        // it is being retrained. The MLShadowArbiterWorker and StrategyWorker both respect
        // this flag and will exclude the model from signal generation until it is cleared
        // by the reactivation flow after a successful retrain.
        var liveModel = await writeDb.Set<MLModel>().FindAsync([model.Id], ct);
        if (liveModel != null) liveModel.IsSuppressed = true;

        // ── Queue emergency retraining run ────────────────────────────────────
        // Check whether an emergency retrain is already queued for this symbol/timeframe
        // to avoid duplicate training runs being submitted on consecutive weekly cycles.
        bool alreadyQueued = await writeDb.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol
                        && r.Timeframe == model.Timeframe
                        && r.IsEmergencyRetrain   // Only deduplicate against emergency retrains, not regular ones
                        && r.Status    == RunStatus.Queued
                        && !r.IsDeleted, ct);

        if (!alreadyQueued)
        {
            // IsEmergencyRetrain = true causes the MLTrainingWorker to prioritise this run
            // ahead of normally scheduled training jobs.
            writeDb.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol              = model.Symbol,
                Timeframe           = model.Timeframe,
                TriggerType         = TriggerType.Scheduled,  // Closest available type; emergency is flagged separately
                Status              = RunStatus.Queued,
                IsEmergencyRetrain  = true,                   // Priority flag for MLTrainingWorker
                FromDate            = DateTime.UtcNow.AddDays(-365), // Retrain on the last full year of data
                ToDate              = DateTime.UtcNow,
                LearnerArchitecture = model.LearnerArchitecture, // Preserve the existing architecture for continuity
            });
        }

        await writeDb.SaveChangesAsync(ct);
    }

    // ── Bai-Perron Sup-F statistic ────────────────────────────────────────────

    /// <summary>
    /// Computes the supremum of F-statistics (Sup-F) over all interior candidate breakpoints
    /// τ ∈ [h·n, (1-h)·n], where h = <see cref="TrimFraction"/> = 0.15.
    /// </summary>
    /// <remarks>
    /// <b>Statistical interpretation:</b><br/>
    /// For each candidate break position τ, the F-statistic tests whether splitting the
    /// outcome series at τ into two segments with separate means produces a significantly
    /// better fit than a single constant mean over the whole series.
    ///
    /// <b>F-statistic formula for a single break at τ:</b>
    /// <code>
    ///   F(τ) = [(SS_all − SS_break) / q] / [SS_break / (n − 2)]
    /// </code>
    /// Where:
    /// <list type="bullet">
    ///   <item><c>SS_all</c>   = Σ (yᵢ − ȳ)²              — total sum of squares (null model).</item>
    ///   <item><c>SS_break</c> = SS₁(τ) + SS₂(τ)           — sum of squares of both segments (break model).</item>
    ///   <item><c>SS₁(τ)</c>  = Σᵢ₌₁ᵗ (yᵢ − ȳ₁)²         — sum of squares within segment 1 [0, τ).</item>
    ///   <item><c>SS₂(τ)</c>  = Σᵢ₌ₜ₊₁ⁿ (yᵢ − ȳ₂)²       — sum of squares within segment 2 [τ, n).</item>
    ///   <item><c>q = 1</c>   — number of restrictions (one additional mean parameter for the break model).</item>
    ///   <item><c>n − 2</c>   — degrees of freedom in the denominator (n observations, 2 means estimated).</item>
    /// </list>
    /// The numerator measures the variance <em>reduction</em> from allowing a break: a large
    /// reduction relative to the residual variance (denominator) is evidence of a true break.
    ///
    /// <b>Sup-F:</b><br/>
    /// The overall test statistic is the maximum F(τ) over all interior τ:
    /// <code>
    ///   SupF = max_τ ∈ [h·n, (1-h)·n] F(τ)
    /// </code>
    /// Taking the maximum creates a composite test that is powerful against breaks at any
    /// unknown date, at the cost of requiring non-standard (tabulated) critical values because
    /// the distribution of the maximum of correlated F-statistics is non-trivial.
    ///
    /// <b>Trimming:</b><br/>
    /// Break positions near the edges of the sample (within h·n = 15% of either end) are
    /// excluded. With too few observations on one side, the segment mean is unreliable and
    /// F-statistics become inflated, causing false positives. The trimming fraction h=0.15
    /// is the standard choice from Andrews (1993) and matches the critical value table used.
    ///
    /// <b>Computational complexity:</b><br/>
    /// This implementation is O(n²) due to the nested inner loops computing SS₁ and SS₂
    /// at each τ. A prefix-sum optimisation could reduce this to O(n), but for the typical
    /// window of 60–200 observations the O(n²) cost is negligible (at most a few thousand
    /// multiplications per model per week).
    /// </remarks>
    /// <param name="y">
    /// Chronologically ordered list of binary outcomes (1.0 = correct, 0.0 = incorrect),
    /// covering the <see cref="WindowDays"/>-day rolling window.
    /// </param>
    /// <returns>
    /// The Sup-F statistic — the maximum F(τ) across all interior candidate break positions.
    /// Returns 0 if the trimmed search region is empty (too few observations).
    /// </returns>
    private static double ComputeSupF(List<double> y)
    {
        int n = y.Count;

        // The interior trimmed region excludes the outer h% from each end to ensure
        // each sub-segment has sufficient observations for a stable mean estimate.
        int minBound = (int)Math.Ceiling(n * TrimFraction); // Earliest valid break position
        int maxBound = n - minBound;                         // Latest valid break position

        // Guard: if trimming leaves no interior region (should not happen with n >= MinObservations),
        // return 0 (no break detected) rather than throwing.
        if (minBound >= maxBound) return 0;

        // ── Compute SS_all: total sum of squares under the null model (no break) ──
        // SS_all = Σ yᵢ² − (Σ yᵢ)² / n  (computational formula for variance × n)
        double sumAll = y.Sum();
        double ssAll  = y.Sum(v => v * v) - sumAll * sumAll / n;

        double supF = 0; // Running maximum — updated at each candidate τ

        for (int tau = minBound; tau <= maxBound; tau++)
        {
            // ── Compute segment sums ───────────────────────────────────────────
            // sum1 = Σᵢ<τ yᵢ  (sum of outcomes in the first segment [0, τ))
            // sum2 = Σᵢ≥τ yᵢ  (sum of outcomes in the second segment [τ, n))
            double sum1 = 0, sum2 = 0;
            for (int i = 0;   i < tau; i++) sum1 += y[i];
            for (int i = tau; i < n;   i++) sum2 += y[i];

            double mean1 = sum1 / tau;        // ȳ₁: sample mean of segment 1
            double mean2 = sum2 / (n - tau);  // ȳ₂: sample mean of segment 2

            // ── Compute within-segment sums of squares ─────────────────────────
            // SS₁ = Σᵢ<τ  (yᵢ − ȳ₁)²  — unexplained variance in segment 1
            // SS₂ = Σᵢ≥τ  (yᵢ − ȳ₂)²  — unexplained variance in segment 2
            double ss1 = 0, ss2 = 0;
            for (int i = 0;   i < tau; i++) ss1 += (y[i] - mean1) * (y[i] - mean1);
            for (int i = tau; i < n;   i++) ss2 += (y[i] - mean2) * (y[i] - mean2);

            // SS_break = SS₁ + SS₂: total residual variance under the break model.
            // A small SS_break relative to SS_all means the break model explains much more variance.
            double ssBreak = ss1 + ss2;

            // Guard against degenerate cases (all outcomes identical, or zero variance).
            if (ssBreak <= 0 || ssAll <= 0) continue;

            // ── F-statistic at this break position τ ──────────────────────────
            // F = ((SS_all − SS_break) / q) / (SS_break / (n − 2))
            //   where q = 1 (one break → one additional mean parameter estimated).
            // Rearranging: F = (SS_all − SS_break) × (n − 2) / SS_break
            double f = ((ssAll - ssBreak) * (n - 2)) / ssBreak;

            // Track the supremum (maximum F over all candidate τ).
            if (f > supF) supF = f;
        }

        return supF;
    }
}
