using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Reconciles multi-timeframe model predictions using MinT (Minimum Trace) to produce
/// mutually consistent probability estimates (Rec #35).
/// </summary>
public interface IMinTReconciler
{
    /// <summary>
    /// Given a dictionary of raw Buy probabilities keyed by timeframe, returns a
    /// reconciled probability dictionary where predictions at different timeframes
    /// are adjusted to be mutually consistent, weighted by their Brier scores.
    /// </summary>
    /// <param name="rawProbabilities">
    /// Dictionary of {timeframe → rawBuyProbability} from active models.
    /// </param>
    /// <param name="brierScores">
    /// Dictionary of {timeframe → brierScore} for weighting (lower brier = more weight).
    /// </param>
    /// <returns>
    /// Reconciled probabilities keyed by timeframe, and the pre-reconciliation disagreement.
    /// </returns>
    (Dictionary<Timeframe, double> Reconciled, double PreReconciliationDisagreement) Reconcile(
        Dictionary<Timeframe, double> rawProbabilities,
        Dictionary<Timeframe, double> brierScores);
}
