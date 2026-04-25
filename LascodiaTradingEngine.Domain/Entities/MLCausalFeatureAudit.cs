using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores the result of a Granger-causality test for a single feature of an
/// <see cref="MLModel"/>. <c>MLCausalFeatureWorker</c> runs bivariate Granger tests
/// on every feature against the return series; features that fail the test
/// (p ≥ 0.05) are candidates for masking in future training runs.
/// </summary>
/// <remarks>
/// Granger causality (F-test) checks whether lagged values of feature f at order
/// <see cref="LagOrder"/> significantly improve prediction of the return series
/// beyond an AR(p) baseline. A low p-value indicates predictive causality.
/// </remarks>
public class MLCausalFeatureAudit : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> this audit relates to.</summary>
    public long      MLModelId          { get; set; }

    /// <summary>Symbol this audit was computed for.</summary>
    public string    Symbol             { get; set; } = string.Empty;

    /// <summary>Timeframe this audit was computed for.</summary>
    public Timeframe Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>
    /// Zero-based index of the feature in the <c>MLFeatureHelper.FeatureNames</c> array.
    /// </summary>
    public int       FeatureIndex       { get; set; }

    /// <summary>Human-readable name of the feature (e.g. "Rsi", "AtrNorm").</summary>
    public string    FeatureName        { get; set; } = string.Empty;

    /// <summary>
    /// F-statistic of the Granger causality test.
    /// Higher values suggest stronger causal influence on returns.
    /// </summary>
    public decimal   GrangerFStat       { get; set; }

    /// <summary>
    /// P-value of the Granger F-test. Stored as <see cref="double"/> so values that span
    /// many orders of magnitude (e.g. <c>1e-15</c> for very strong signals up to <c>1.0</c>
    /// for null) round-trip without underflow. Values &lt; <c>MLCausal:FdrAlpha</c> on the
    /// post-correction q-value are flagged via <see cref="IsCausal"/>; the raw p-value here
    /// is the un-corrected per-feature F-test result.
    /// </summary>
    public double    GrangerPValue      { get; set; }

    /// <summary>
    /// Multiple-comparison-corrected q-value (BH-FDR by default; Benjamini-Yekutieli when
    /// <c>MLCausal:FdrProcedure</c> is set to <c>BenjaminiYekutieli</c>). Comparable across
    /// features within the same model — a feature with raw <c>GrangerPValue=0.04</c> may have
    /// <c>GrangerQValue=0.18</c> after correction across many features. <see cref="IsCausal"/>
    /// reflects the q-value comparison, not the raw p-value. Stored as <see cref="double"/>
    /// for the same precision reasons as <see cref="GrangerPValue"/>.
    /// </summary>
    public double    GrangerQValue      { get; set; }

    /// <summary>
    /// Lag order (number of lags) used in the unrestricted VAR model.
    /// Determined by AIC minimisation up to lag 10.
    /// </summary>
    public int       LagOrder           { get; set; }

    /// <summary>
    /// <c>true</c> when GrangerPValue &lt; 0.05 — feature Granger-causes returns
    /// and should be retained. <c>false</c> means the feature may be spurious.
    /// </summary>
    public bool      IsCausal           { get; set; }

    /// <summary>
    /// When <c>true</c>, this feature has been added to the run's
    /// <c>HyperparamOverrides.DisabledFeatureIndices</c> for the next training batch.
    /// </summary>
    public bool      IsMaskedForTraining { get; set; }

    /// <summary>UTC timestamp when this audit was computed.</summary>
    public DateTime  ComputedAt         { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag.</summary>
    public bool      IsDeleted          { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>The model this causal audit belongs to.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
