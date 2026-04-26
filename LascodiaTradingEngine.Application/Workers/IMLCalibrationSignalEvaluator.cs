using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Pure-functional calibration math + signal/severity classification, extracted from
/// <see cref="MLCalibrationMonitorWorker"/> as an injectable collaborator so the math
/// can be unit-tested without spinning up the worker. The default implementation is
/// <see cref="MLCalibrationSignalEvaluator"/>; tests or alternative scoring strategies
/// can substitute their own.
/// </summary>
public interface IMLCalibrationSignalEvaluator
{
    /// <summary>
    /// Computes the per-window calibration summary (ECE, per-bin reliability,
    /// time-decay-weighted accuracy/confidence, bootstrap stderr) from a sample window.
    /// When <paramref name="cachedStderr"/> is non-null, it's used in place of running a
    /// fresh bootstrap (caller has decided the cache is fresh). <paramref name="modelId"/>
    /// is mixed into the bootstrap RNG seed so two models with identical sample boundaries
    /// don't share a sequence.
    /// </summary>
    CalibrationSummary ComputeSummary(
        IReadOnlyList<CalibrationSample> samples,
        int bootstrapResamples,
        DateTime nowUtc,
        double timeDecayHalfLifeDays,
        int minSamplesForTimeDecay,
        double? cachedStderr,
        long modelId);

    /// <summary>
    /// Builds the three discriminated signals (Threshold / Trend / Baseline) from a current
    /// ECE plus prior context. Trend is K-sigma-stderr gated.
    /// </summary>
    CalibrationSignals BuildSignals(
        double currentEce,
        double eceStderr,
        double? previousEce,
        double? baselineEce,
        double maxEce,
        double degradationDelta,
        double regressionGuardK);

    /// <summary>
    /// Classifies an evaluated window as None / Warning / Critical based on which signals
    /// fired and whether the magnitudes cross the severe-tier multiplier.
    /// </summary>
    MLCalibrationMonitorAlertState ResolveAlertState(
        double currentEce,
        CalibrationSignals signals,
        double maxEce,
        double degradationDelta);

    /// <summary>
    /// Maps the alert state to an <see cref="AlertSeverity"/> using current ECE and signal
    /// deltas as graduation thresholds.
    /// </summary>
    AlertSeverity DetermineSeverity(
        MLCalibrationMonitorAlertState alertState,
        CalibrationSummary summary,
        CalibrationSignals signals,
        double maxEce,
        double degradationDelta);
}
