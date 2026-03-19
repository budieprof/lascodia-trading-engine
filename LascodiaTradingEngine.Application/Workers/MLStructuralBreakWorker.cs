using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects abrupt structural breaks in a model's live prediction-outcome distribution
/// using the Bai-Perron sequential breakpoint test. When a break is confirmed at the
/// 99% significance level, the model is suppressed and an emergency retraining run is queued.
/// </summary>
/// <remarks>
/// Unlike CUSUM (which detects gradual drift), the Bai-Perron test identifies the
/// <em>exact date</em> of an abrupt change in the return-generating process
/// (e.g., a central-bank pivot or liquidity crisis). The test is applied to the
/// rolling 90-day series of resolved <c>MLModelPredictionLog.DirectionCorrect</c>
/// outcomes (1 = correct, 0 = incorrect) as a Bernoulli sequence.
///
/// The F-statistic for a single break at position τ is compared to the 99% critical
/// value of the Sup-F distribution. If the maximum F-stat exceeds the threshold,
/// a break is declared and emergency retraining is triggered.
/// </remarks>
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

    public MLStructuralBreakWorker(
        ILogger<MLStructuralBreakWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLStructuralBreakWorker starting");
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "MLStructuralBreakWorker cycle failed"); }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb       = readCtx.GetDbContext();
        var writeDb      = writeCtx.GetDbContext();

        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsSuppressed && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            await TestModelAsync(model, readDb, writeDb, ct);
        }
    }

    private async Task TestModelAsync(
        MLModel model,
        Microsoft.EntityFrameworkCore.DbContext readDb,
        Microsoft.EntityFrameworkCore.DbContext writeDb,
        CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-WindowDays);

        var outcomes = await readDb.Set<MLModelPredictionLog>()
            .Where(p => p.MLModelId == model.Id
                     && p.DirectionCorrect != null
                     && p.PredictedAt >= cutoff
                     && !p.IsDeleted)
            .OrderBy(p => p.PredictedAt)
            .Select(p => p.DirectionCorrect!.Value ? 1.0 : 0.0)
            .ToListAsync(ct);

        if (outcomes.Count < MinObservations)
        {
            _logger.LogDebug("Model {Id}: insufficient observations ({N}) for Bai-Perron test", model.Id, outcomes.Count);
            return;
        }

        double supF = ComputeSupF(outcomes);

        _logger.LogDebug("Model {Id} Sup-F = {SupF:F4} (threshold {Threshold})", model.Id, supF, SupFCritical99);

        if (supF < SupFCritical99) return;

        _logger.LogWarning(
            "Structural break detected for model {Id} ({Symbol}/{Timeframe}) — Sup-F={SupF:F2} > {Critical}. Suppressing and queuing emergency retrain.",
            model.Id, model.Symbol, model.Timeframe, supF, SupFCritical99);

        // Suppress the model
        var liveModel = await writeDb.Set<MLModel>().FindAsync([model.Id], ct);
        if (liveModel != null) liveModel.IsSuppressed = true;

        // Queue emergency retraining run
        bool alreadyQueued = await writeDb.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol
                        && r.Timeframe == model.Timeframe
                        && r.IsEmergencyRetrain
                        && r.Status    == RunStatus.Queued
                        && !r.IsDeleted, ct);

        if (!alreadyQueued)
        {
            writeDb.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol              = model.Symbol,
                Timeframe           = model.Timeframe,
                TriggerType         = TriggerType.Scheduled,
                Status              = RunStatus.Queued,
                IsEmergencyRetrain  = true,
                FromDate            = DateTime.UtcNow.AddDays(-365),
                ToDate              = DateTime.UtcNow,
                LearnerArchitecture = model.LearnerArchitecture,
            });
        }

        await writeDb.SaveChangesAsync(ct);
    }

    // ── Bai-Perron Sup-F statistic ────────────────────────────────────────────

    /// <summary>
    /// Computes the supremum of F-statistics over all interior breakpoints τ ∈ [h*n, (1-h)*n].
    /// Each F-stat tests H₀: no break at τ vs H₁: mean shift at τ.
    /// </summary>
    private static double ComputeSupF(List<double> y)
    {
        int n = y.Count;
        int minBound = (int)Math.Ceiling(n * TrimFraction);
        int maxBound = n - minBound;

        if (minBound >= maxBound) return 0;

        double sumAll = y.Sum();
        double ssAll  = y.Sum(v => v * v) - sumAll * sumAll / n;

        double supF = 0;

        for (int tau = minBound; tau <= maxBound; tau++)
        {
            double sum1 = 0, sum2 = 0;
            for (int i = 0;   i < tau; i++) sum1 += y[i];
            for (int i = tau; i < n;   i++) sum2 += y[i];

            double mean1 = sum1 / tau;
            double mean2 = sum2 / (n - tau);

            double ss1 = 0, ss2 = 0;
            for (int i = 0;   i < tau; i++) ss1 += (y[i] - mean1) * (y[i] - mean1);
            for (int i = tau; i < n;   i++) ss2 += (y[i] - mean2) * (y[i] - mean2);

            double ssBreak = ss1 + ss2;
            if (ssBreak <= 0 || ssAll <= 0) continue;

            // F = ((ssAll - ssBreak)/q) / (ssBreak/(n-2))  where q=1 (one break)
            double f = ((ssAll - ssBreak) * (n - 2)) / ssBreak;
            if (f > supF) supF = f;
        }

        return supF;
    }
}
