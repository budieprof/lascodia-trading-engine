using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Applies the ADWIN (ADaptive WINdowing) drift detection algorithm to the binary
/// directional-accuracy outcomes of each active ML model. Runs once per day.
///
/// <para>
/// <b>What ADWIN detects:</b><br/>
/// ADWIN is a data-stream algorithm that maintains a variable-length window of recent
/// observations and automatically shrinks the window whenever a statistically significant
/// change in the mean is detected between any two sub-windows. This makes it self-adapting:
/// unlike fixed-window detectors, it does not require the operator to specify how "recent"
/// the monitoring window should be.
/// </para>
///
/// <para>
/// <b>Algorithm (simplified):</b><br/>
/// Given a window W of n binary outcomes (1=correct, 0=incorrect), the test exhausts all
/// valid split points t ∈ [30, n−30] and asks: "Is the mean in the left sub-window [0..t)
/// statistically different from the mean in the right sub-window [t..n)?"
///
/// The cut-off epsilon (ε_cut) at each split point t is derived from Hoeffding's inequality:
/// <code>
/// ε_cut(t) = sqrt( (1/(2·n₁) + 1/(2·n₂)) · ln(4·n / δ) )
/// </code>
/// Where:
/// <list type="bullet">
///   <item><c>n₁ = t</c> — size of the left (older) sub-window.</item>
///   <item><c>n₂ = n − t</c> — size of the right (newer) sub-window.</item>
///   <item><c>n = n₁ + n₂</c> — total window size.</item>
///   <item><c>δ</c> — the false-positive confidence parameter (default 0.002, i.e., 0.2% chance of false alarm per test).</item>
/// </list>
/// Drift is declared at the first split point t where <c>|μ₁ − μ₂| &gt; ε_cut(t)</c>.
/// </para>
///
/// <para>
/// <b>Implementation notes:</b><br/>
/// The full ADWIN algorithm uses a bucket-based geometric partition to achieve O(log n)
/// time per observation. This implementation uses prefix sums for an O(n) scan over the
/// fixed 100-observation window, which is equivalent in outcome but simpler to audit.
/// The prefix-sum approach is efficient enough given the daily run cadence and the small
/// fixed window size.
/// </para>
///
/// <para>
/// <b>Pipeline position:</b><br/>
/// ADWIN complements the fixed-window detectors (<see cref="MLCusumDriftWorker"/>,
/// <see cref="MLMultiScaleDriftWorker"/>) by removing the need to choose a window size.
/// Its results are recorded in <c>MLAdwinDriftLog</c> for auditing and trend analysis.
/// When drift is detected, the worker queues a retraining run with
/// <c>DriftTriggerType = "AdwinDrift"</c> (deduplicated — skips if a run is already
/// queued/running for the same symbol/timeframe). The ADWIN drift signal also feeds
/// into <see cref="MLModelRetirementWorker"/> as a 4th retirement signal via the
/// <c>MLDrift:{Symbol}:{Tf}:AdwinDriftDetected</c> config key.
/// </para>
///
/// <para>
/// <b>Constants:</b>
/// <list type="table">
///   <listheader><term>Constant</term><description>Value / Description</description></listheader>
///   <item><term><c>MinLogs</c></term><description>60 — minimum resolved predictions to run the test.</description></item>
///   <item><term><c>LogTake</c></term><description>100 — fixed window size (most recent N outcomes).</description></item>
///   <item><term><c>Delta</c></term><description>0.002 — ADWIN δ parameter (false-positive rate per test invocation).</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class MLAdwinDriftWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLAdwinDriftWorker> _logger;

    /// <summary>Minimum number of resolved prediction logs required before the ADWIN test is applied.</summary>
    private const int    MinLogs  = 60;

    /// <summary>
    /// Number of most-recent resolved outcomes to include in the ADWIN window.
    /// A fixed window of 100 balances statistical power against memory and compute cost.
    /// </summary>
    private const int    LogTake  = 100;

    /// <summary>
    /// ADWIN δ (delta) confidence parameter. Controls the trade-off between detection speed
    /// and false-positive rate. δ = 0.002 means the probability of a false alarm on any single
    /// test invocation is at most 0.2%. Smaller δ → fewer false positives but slower detection.
    /// From Bifet &amp; Gavaldà (2007) "Learning from Time-Changing Data with Adaptive Windowing".
    /// </summary>
    private const double Delta    = 0.002;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// DI scope factory. A new scope is created per run cycle so EF Core contexts are
    /// properly disposed and do not accumulate stale tracked entities across days.
    /// </param>
    /// <param name="logger">Structured logger for diagnostic and warning output.</param>
    public MLAdwinDriftWorker(IServiceScopeFactory scopeFactory, ILogger<MLAdwinDriftWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure on application start.
    /// Runs the ADWIN drift scan once per day, permanently, until the application shuts down.
    /// </summary>
    /// <remarks>
    /// The 24-hour cadence is appropriate for ADWIN because:
    /// <list type="bullet">
    ///   <item>ADWIN operates on accumulated outcomes, not tick-level streams; daily granularity matches the typical drift timescale in FX markets.</item>
    ///   <item>Results are logged to <c>MLAdwinDriftLog</c> for analysis; the detection signal is used by downstream workers and operators rather than directly triggering emergency retrains.</item>
    /// </list>
    /// </remarks>
    /// <param name="stoppingToken">Graceful-shutdown token from the .NET host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLAdwinDriftWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLAdwinDriftWorker error"); }
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    /// <summary>
    /// Executes one full ADWIN drift scan pass across all active, non-meta-learner,
    /// non-MAML-initializer models. For each eligible model, loads its resolved prediction
    /// history, runs the ADWIN split-point scan, and writes a <c>MLAdwinDriftLog</c> row.
    /// </summary>
    /// <remarks>
    /// Meta-learner and MAML-initializer models are excluded because their "accuracy"
    /// is not directly comparable to a binary directional outcome — they serve ensemble
    /// coordination roles rather than generating primary trading signals.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Exclude meta-learners and MAML initializers — their outputs are not comparable
        // to a simple correct/incorrect directional prediction.
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Load all resolved prediction logs for this model in chronological order.
            // Ascending sort ensures that logs.GetRange(end-N, N) gives the most recent N records.
            var logs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == model.Id && l.DirectionCorrect.HasValue && !l.IsDeleted)
                .OrderBy(l => l.PredictedAt)
                .ToListAsync(ct);

            // Skip models with insufficient history — ADWIN needs at least 2×30 observations
            // (30 on each side of any candidate split point) to be statistically meaningful.
            if (logs.Count < MinLogs) continue;

            // ── Extract the fixed-size ADWIN window ────────────────────────────
            // Use only the LogTake most recent records. This is the "current window" W
            // that ADWIN scans for distributional shifts.
            var recent = logs.Count > LogTake ? logs.GetRange(logs.Count - LogTake, LogTake) : logs;
            int n = recent.Count;

            // Convert boolean outcomes to a binary integer array for numerical processing.
            // 1 = correct prediction, 0 = incorrect prediction.
            int[] outcomes = recent.Select(l => l.DirectionCorrect == true ? 1 : 0).ToArray();

            // State tracking for the best (first-detected) drift split point.
            bool   driftDetected = false;
            int    bestT         = n / 2;   // Split index where drift was first detected (or midpoint if none)
            double bestEpsilon   = 0;        // ε_cut at the detected split point
            double bestMean1     = 0;        // μ₁ — mean accuracy of the older sub-window
            double bestMean2     = 0;        // μ₂ — mean accuracy of the newer sub-window

            // ── Precompute prefix sums for O(1) sub-window mean computation ───
            // prefix[i] = sum of outcomes[0..i-1]
            // Sub-window mean [a, b) = (prefix[b] - prefix[a]) / (b - a)
            double[] prefix = new double[n + 1];
            for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + outcomes[i];

            // ── ADWIN split-point scan ─────────────────────────────────────────
            // Test every candidate split point t in [30, n-30] to ensure both sub-windows
            // have at least 30 observations, which is the minimum for the Hoeffding bound
            // to be reliable for Bernoulli random variables with p ∈ [0, 1].
            for (int t = 30; t <= n - 30; t++)
            {
                int    m1  = t;       // Left (older) sub-window size
                int    m2  = n - t;   // Right (newer) sub-window size

                // Compute sub-window accuracy means using prefix sums (O(1) per split).
                double mu1 = prefix[t] / m1;                             // μ₁ = mean of outcomes[0..t)
                double mu2 = (prefix[n] - prefix[t]) / m2;              // μ₂ = mean of outcomes[t..n)

                // ── Hoeffding-based ε_cut formula ────────────────────────────────
                // Derived from the ADWIN paper (Bifet & Gavaldà, 2007).
                // ε_cut is the minimum |μ₁ − μ₂| that is statistically significant at level δ,
                // given the sub-window sizes n₁ and n₂.
                // Formula: ε = sqrt( (1/(2n₁) + 1/(2n₂)) · ln(4n/δ) )
                // The factor 4n in the log term accounts for the union bound over all O(n)
                // candidate split points, keeping the overall false-positive rate at most δ.
                double eps = Math.Sqrt((1.0 / (2 * m1) + 1.0 / (2 * m2)) * Math.Log(4.0 * n / Delta));

                // ── Drift decision ────────────────────────────────────────────────
                // |μ₁ − μ₂| > ε_cut: the difference between the two sub-windows cannot
                // be explained by chance at confidence level 1 − δ. Declare drift.
                if (Math.Abs(mu1 - mu2) > eps)
                {
                    driftDetected = true;
                    bestT         = t;        // Record the split point index where drift was found
                    bestEpsilon   = eps;      // The ε_cut at this split — included in the log for auditing
                    bestMean1     = mu1;      // Older sub-window mean (before the shift)
                    bestMean2     = mu2;      // Newer sub-window mean (after the shift)
                    break;                    // Stop at the first detected split (earliest drift point)
                }
            }

            // ── Persist ADWIN log entry ────────────────────────────────────────
            // Always write a log row regardless of whether drift was detected.
            // This provides a complete daily time series for auditing and retrospective analysis.
            writeDb.Set<MLAdwinDriftLog>().Add(new MLAdwinDriftLog
            {
                MLModelId     = model.Id,
                Symbol        = model.Symbol,
                Timeframe     = model.Timeframe,
                DriftDetected = driftDetected,
                Window1Mean   = bestMean1,    // μ₁ (older sub-window accuracy, or 0 if no drift)
                Window2Mean   = bestMean2,    // μ₂ (newer sub-window accuracy, or 0 if no drift)
                EpsilonCut    = bestEpsilon,  // Hoeffding ε_cut at the detected split
                Window1Size   = bestT,        // Left sub-window size at detected split (or n/2 if no drift)
                Window2Size   = n - bestT,    // Right sub-window size
                DetectedAt    = DateTime.UtcNow
            });

            if (driftDetected)
            {
                _logger.LogWarning(
                    "MLAdwinDriftWorker: {S}/{T} ADWIN drift detected — |{M1:F4} - {M2:F4}| > ε={E:F4}.",
                    model.Symbol, model.Timeframe, bestMean1, bestMean2, bestEpsilon);

                // ── Set retirement signal flag ──────────────────────────────────
                // Persists a flag that MLModelRetirementWorker reads as a 4th
                // degradation signal. Expires after 48 hours if not refreshed.
                var adwinFlagKey = $"MLDrift:{model.Symbol}:{model.Timeframe}:AdwinDriftDetected";
                var expiresAt = DateTime.UtcNow.AddHours(48).ToString("O");
                int updated = await writeDb.Set<EngineConfig>()
                    .Where(c => c.Key == adwinFlagKey)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, expiresAt), ct);
                if (updated == 0)
                {
                    writeDb.Set<EngineConfig>().Add(new EngineConfig
                    {
                        Key      = adwinFlagKey,
                        Value    = expiresAt,
                        DataType = ConfigDataType.String,
                    });
                }

                // ── Queue retraining run (deduplicated) ─────────────────────────
                bool alreadyQueued = await readDb.Set<MLTrainingRun>()
                    .AnyAsync(r => r.Symbol    == model.Symbol &&
                                   r.Timeframe == model.Timeframe &&
                                   (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

                if (!alreadyQueued)
                {
                    int trainingDays = await GetConfigAsync<int>(readDb, "MLTraining:TrainingDataWindowDays", 365, ct);
                    var now = DateTime.UtcNow;
                    writeDb.Set<MLTrainingRun>().Add(new MLTrainingRun
                    {
                        Symbol            = model.Symbol,
                        Timeframe         = model.Timeframe,
                        TriggerType       = TriggerType.AutoDegrading,
                        Status            = RunStatus.Queued,
                        FromDate          = now.AddDays(-trainingDays),
                        ToDate            = now,
                        StartedAt         = now,
                        DriftTriggerType  = "AdwinDrift",
                        DriftMetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            detector   = "ADWIN",
                            window1Mean = bestMean1,
                            window2Mean = bestMean2,
                            epsilonCut  = bestEpsilon,
                            splitPoint  = bestT,
                            windowSize  = n,
                        }),
                        Priority = 1, // Drift-triggered = high priority
                    });

                    _logger.LogWarning(
                        "MLAdwinDriftWorker: queued retraining for {S}/{T} (ADWIN drift, μ₁={M1:F4}, μ₂={M2:F4})",
                        model.Symbol, model.Timeframe, bestMean1, bestMean2);
                }
            }
            else
            {
                _logger.LogInformation(
                    "MLAdwinDriftWorker: {S}/{T} no drift detected (n={N}).",
                    model.Symbol, model.Timeframe, n);

                // Clear the retirement signal flag when no drift is detected
                var adwinFlagKey = $"MLDrift:{model.Symbol}:{model.Timeframe}:AdwinDriftDetected";
                await writeDb.Set<EngineConfig>()
                    .Where(c => c.Key == adwinFlagKey)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, (string?)null), ct);
            }

            // Save the log entry immediately per model so a failure on a later model
            // does not roll back successfully recorded results for earlier models.
            await writeDb.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or its string value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
