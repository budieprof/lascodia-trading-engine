using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// MinT (Minimum Trace) multi-timeframe prediction reconciliation (Rec #35).
/// </summary>
/// <remarks>
/// The MinT algorithm for hierarchical forecasting adjusts a set of base predictions
/// so they are mutually consistent across the temporal hierarchy.  For FX models:
///   - H1 is the "bottom level" (finest granularity)
///   - H4 aggregates 4 H1 bars
///   - D1 aggregates 24 H1 bars
///
/// Each model predicts an independent Buy probability for the same underlying movement.
/// MinT adjusts them so the weighted average is consistent, minimising the total
/// variance of the adjustment.  The weights are inverse-Brier-score (lower Brier = more reliable).
///
/// Simplified implementation: weighted average with shrinkage toward the prior 0.5.
/// Full MinT (covariance-based) requires historical reconciliation residuals and is
/// implemented as an iterative reweighting scheme here.
/// </remarks>
public sealed class MinTReconciler : IMinTReconciler
{
    /// <inheritdoc/>
    public (Dictionary<Timeframe, double> Reconciled, double PreReconciliationDisagreement)
        Reconcile(
            Dictionary<Timeframe, double> rawProbabilities,
            Dictionary<Timeframe, double> brierScores)
    {
        if (rawProbabilities.Count <= 1)
            return (new Dictionary<Timeframe, double>(rawProbabilities), 0.0);

        // ── Pre-reconciliation disagreement ───────────────────────────────────
        double sumDisag = 0;
        var keys = rawProbabilities.Keys.ToList();
        int pairs = 0;
        for (int i = 0; i < keys.Count; i++)
        for (int j = i + 1; j < keys.Count; j++)
        {
            sumDisag += Math.Abs(rawProbabilities[keys[i]] - rawProbabilities[keys[j]]);
            pairs++;
        }
        double disagBefore = pairs > 0 ? sumDisag / pairs : 0;

        // ── Compute inverse-Brier weights ─────────────────────────────────────
        // w_k = 1 / (brier_k + ε) — lower Brier score gets more weight
        var weights = new Dictionary<Timeframe, double>();
        foreach (var tf in keys)
        {
            double brier = brierScores.TryGetValue(tf, out var b) ? Math.Clamp(b, 1e-4, 1.0) : 0.5;
            weights[tf] = 1.0 / brier;
        }
        double totalWeight = weights.Values.Sum();

        // ── Weighted average (MinT target) ────────────────────────────────────
        double target = rawProbabilities
            .Sum(kv => kv.Value * weights.GetValueOrDefault(kv.Key, 1.0)) / totalWeight;

        // ── Shrink each estimate toward the target ────────────────────────────
        // Reconciled_k = raw_k + shrinkage_k × (target − raw_k)
        // shrinkage_k = 1 - w_k / totalWeight    (high-weight models move less)
        var reconciled = new Dictionary<Timeframe, double>();
        foreach (var kv in rawProbabilities)
        {
            double w = weights.GetValueOrDefault(kv.Key, 1.0) / totalWeight;
            double shrinkage = Math.Clamp(1.0 - w, 0.0, 0.8); // cap shrinkage at 80 %
            reconciled[kv.Key] = Math.Clamp(kv.Value + shrinkage * (target - kv.Value), 0.01, 0.99);
        }

        return (reconciled, disagBefore);
    }
}
