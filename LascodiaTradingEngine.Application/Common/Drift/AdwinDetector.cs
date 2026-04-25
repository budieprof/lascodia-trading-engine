namespace LascodiaTradingEngine.Application.Common.Drift;

/// <summary>
/// Streaming drift detector based on Bifet & Gavaldà (2007),
/// "Learning from Time-Changing Data with Adaptive Windowing" (SDM 2007).
/// </summary>
/// <remarks>
/// <para>
/// Operates on a binary outcome stream (e.g. directional accuracy: 1 = correct,
/// 0 = wrong). Tries every admissible split point in the window and computes the
/// Hoeffding-bound cut <c>ε</c>. Drift is declared when the older sub-window's mean
/// exceeds the newer sub-window's mean by more than <c>ε</c> (degradation), or
/// when their absolute difference exceeds <c>ε</c> (auditable change of any
/// direction).
/// </para>
/// <para>
/// The Hoeffding bound used is the canonical ADWIN variant from the paper:
/// <code>
///     ε = sqrt( (1 / (2·m)) · ln(2·n / δ') )
/// </code>
/// where <c>m = harmonic_mean(n0, n1)</c>, <c>n = n0 + n1</c>, and
/// <c>δ' = δ / n</c> is the per-split confidence after a Bonferroni-style union
/// bound across all admissible cuts. The split-time correction collapses to the
/// commonly-quoted form <c>ε = sqrt( (1/(2·n0) + 1/(2·n1)) · ln(2·n² / δ) )</c>.
/// See §3.2 (Theorem 3.1) of the paper.
/// </para>
/// <para>
/// The detector is pure / side-effect-free: callers feed in the window of
/// outcomes and receive structured evidence back. Tests therefore do not need a
/// database, a clock, or any worker harness.
/// </para>
/// </remarks>
public static class AdwinDetector
{
    /// <summary>
    /// Minimum size of either sub-window. Splits with fewer than this many
    /// observations on either side are skipped — variance estimates become
    /// unreliable and the Hoeffding bound dominates the signal.
    /// </summary>
    public const int MinSplitSize = 30;

    /// <summary>Total observations required before any split can be considered.</summary>
    public const int MinRequiredObservations = MinSplitSize * 2;

    private const double ScoreTieTolerance = 1e-12;

    /// <summary>
    /// Evaluates the binary outcome stream and returns the strongest evidence found,
    /// or <c>null</c> when there are not enough observations to run any split.
    /// </summary>
    /// <param name="outcomes">
    /// Outcomes ordered chronologically (oldest first). <c>true</c> = success
    /// (e.g. directional prediction was correct), <c>false</c> = failure.
    /// </param>
    /// <param name="delta">
    /// Confidence parameter. Smaller values demand stronger evidence before
    /// declaring drift. Must be in <c>(0, 1)</c>; the worker clamps operator
    /// inputs to a safe range before calling.
    /// </param>
    public static AdwinScanResult? Evaluate(
        IReadOnlyList<bool> outcomes,
        double delta)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        int n = outcomes.Count;
        if (n < MinRequiredObservations)
            return null;
        if (delta <= 0.0 || delta >= 1.0 || !double.IsFinite(delta))
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "delta must be in (0, 1).");

        double[] prefix = new double[n + 1];
        for (int i = 0; i < n; i++)
            prefix[i + 1] = prefix[i] + (outcomes[i] ? 1.0 : 0.0);

        AdwinEvidence? bestDegradingEvidence = null;
        double bestDegradingScore = double.NegativeInfinity;

        AdwinEvidence? bestAuditEvidence = null;
        double bestAuditScore = double.NegativeInfinity;

        // Bonferroni union bound: each of the n candidate split points adds a
        // hypothesis test, so we tighten δ by 1/n per split. This is the
        // adjustment from §3 of Bifet & Gavaldà (2007).
        double deltaPrime = delta / n;

        for (int split = MinSplitSize; split <= n - MinSplitSize; split++)
        {
            int n0 = split;
            int n1 = n - split;

            double mu0 = prefix[split] / n0;
            double mu1 = (prefix[n] - prefix[split]) / n1;

            // ε = sqrt( (1/(2·n0) + 1/(2·n1)) · ln(2/δ') )
            // Equivalent to the harmonic-mean form sqrt( (1/(2m)) · ln(2/δ') )
            // and matches the ε_cut formula in §3.2 of the paper.
            double epsilonCut = Math.Sqrt(
                (1.0 / (2.0 * n0) + 1.0 / (2.0 * n1)) *
                Math.Log(2.0 / deltaPrime));

            double degradingScore = (mu0 - mu1) - epsilonCut;
            var degradingEvidence = new AdwinEvidence(split, mu0, mu1, epsilonCut, degradingScore);

            if (degradingScore > bestDegradingScore + ScoreTieTolerance ||
                (Math.Abs(degradingScore - bestDegradingScore) <= ScoreTieTolerance &&
                 bestDegradingEvidence is { } currentDegrading &&
                 split > currentDegrading.SplitIndex))
            {
                bestDegradingScore = degradingScore;
                bestDegradingEvidence = degradingEvidence;
            }

            double auditScore = Math.Abs(mu0 - mu1) - epsilonCut;
            if (auditScore > bestAuditScore + ScoreTieTolerance ||
                (Math.Abs(auditScore - bestAuditScore) <= ScoreTieTolerance &&
                 bestAuditEvidence is { } currentAudit &&
                 split > currentAudit.SplitIndex))
            {
                bestAuditScore = auditScore;
                bestAuditEvidence = degradingEvidence with { Score = auditScore };
            }
        }

        if (bestAuditEvidence is null)
            return null;

        var auditEvidence = bestAuditEvidence.Value;
        var degradingEvidence2 = bestDegradingEvidence.GetValueOrDefault();
        bool driftDetected = bestDegradingEvidence.HasValue && bestDegradingScore > 0.0;
        var selectedEvidence = driftDetected ? degradingEvidence2 : auditEvidence;
        double accuracyDrop = driftDetected
            ? Math.Max(0.0, selectedEvidence.Window1Mean - selectedEvidence.Window2Mean)
            : 0.0;

        return new AdwinScanResult(driftDetected, selectedEvidence, accuracyDrop);
    }
}

/// <summary>
/// Numeric evidence from a single ADWIN split-point evaluation. Persisted on the
/// audit row so degradation magnitude can be charted over time.
/// </summary>
public readonly record struct AdwinEvidence(
    int SplitIndex,
    double Window1Mean,
    double Window2Mean,
    double EpsilonCut,
    double Score);

/// <summary>The outcome of a full ADWIN scan over a binary outcome window.</summary>
public readonly record struct AdwinScanResult(
    bool DriftDetected,
    AdwinEvidence SelectedEvidence,
    double AccuracyDrop);
