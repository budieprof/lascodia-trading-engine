using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

// Partial: audit pipeline + diagnostics builders. See file-layout note in
// MLCalibrationMonitorWorker.cs.
public sealed partial class MLCalibrationMonitorWorker
{
    private static void EnqueueAudit(
        List<MLCalibrationLog> pending,
        ActiveModelCandidate model,
        MarketRegimeEnum? regime,
        string outcome,
        string reason,
        CalibrationSummary summary,
        CalibrationSignals signals,
        MLCalibrationMonitorAlertState alertState,
        DateTime? newestOutcomeAt,
        string diagnostics,
        DateTime evaluatedAt)
    {
        pending.Add(new MLCalibrationLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            Regime = regime,
            EvaluatedAt = evaluatedAt,
            Outcome = Truncate(outcome, 32),
            Reason = Truncate(reason, 64),
            ResolvedSampleCount = summary.ResolvedCount,
            CurrentEce = summary.CurrentEce,
            PreviousEce = signals.PreviousEce,
            BaselineEce = signals.BaselineEce,
            TrendDelta = signals.TrendDelta,
            BaselineDelta = signals.BaselineDelta,
            Accuracy = summary.Accuracy,
            MeanConfidence = summary.MeanConfidence,
            EceStderr = summary.EceStderr,
            ThresholdExceeded = signals.ThresholdExceeded,
            TrendExceeded = signals.TrendExceeded,
            BaselineExceeded = signals.BaselineExceeded,
            AlertState = alertState switch
            {
                MLCalibrationMonitorAlertState.Critical => "critical",
                MLCalibrationMonitorAlertState.Warning => "warning",
                _ => "none",
            },
            NewestOutcomeAt = newestOutcomeAt,
            DiagnosticsJson = Truncate(diagnostics, MaxAuditDiagnosticsLength),
        });
    }

    private async Task FlushAuditsAsync(IEnumerable<MLCalibrationLog> pending, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
            // AddRangeAsync enumerates `pending` once. Both List<T> (per-model mode) and
            // ConcurrentBag<T> (cycle mode) are valid inputs; the bag is enumerated as a
            // snapshot at the time of the call, which is fine here because no further
            // additions happen after the parallel block returns.
            await db.Set<MLCalibrationLog>().AddRangeAsync(pending, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to persist calibration audit row(s); rows discarded.",
                WorkerName);
        }
    }

    private static string BuildDiagnostics(params (string Key, object Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return JsonSerializer.Serialize(dict);
    }

    private static string BuildDiagnosticsWithBins(
        CalibrationSummary summary,
        CalibrationSignals signals,
        MLCalibrationMonitorWorkerSettings settings,
        bool bootstrapCacheHit)
    {
        var bins = new List<object>(NumBins);
        for (int i = 0; i < NumBins; i++)
        {
            bins.Add(new
            {
                index = i,
                count = summary.BinCounts?[i] ?? 0,
                accuracy = Math.Round(summary.BinAccuracy?[i] ?? 0, 6),
                meanConfidence = Math.Round(summary.BinMeanConfidence?[i] ?? 0, 6),
            });
        }

        return JsonSerializer.Serialize(new
        {
            ece = Math.Round(summary.CurrentEce, 6),
            eceStderr = Math.Round(summary.EceStderr, 6),
            accuracy = Math.Round(summary.Accuracy, 6),
            meanConfidence = Math.Round(summary.MeanConfidence, 6),
            trendDelta = Math.Round(signals.TrendDelta, 6),
            baselineDelta = Math.Round(signals.BaselineDelta, 6),
            regressionGuardK = Math.Round(settings.RegressionGuardK, 6),
            trendStderrPasses = signals.TrendStderrPasses,
            thresholdExceeded = signals.ThresholdExceeded,
            trendExceeded = signals.TrendExceeded,
            baselineExceeded = signals.BaselineExceeded,
            bootstrapCacheHit,
            bins,
        });
    }
}
