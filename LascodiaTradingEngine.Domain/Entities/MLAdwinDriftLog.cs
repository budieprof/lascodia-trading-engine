using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records the outcome of an ADWIN (ADaptive WINdowing) drift scan for a specific
/// ML model, symbol, and timeframe.
/// </summary>
/// <remarks>
/// <para>
/// ADWIN monitors a stream of binary prediction outcomes where <c>1</c> means the
/// model predicted the market direction correctly and <c>0</c> means it was wrong.
/// The detector compares an older sub-window against a newer sub-window and raises
/// drift when the difference in their mean accuracy is larger than the Hoeffding
/// confidence bound stored in <see cref="EpsilonCut"/>.
/// </para>
/// <para>
/// <c>MLAdwinDriftWorker</c> writes
/// one row per evaluated model each run, regardless of whether drift was detected.
/// This makes the table useful both as an audit trail for retraining/retirement
/// decisions and as a daily time series for reviewing model stability.
/// </para>
/// </remarks>
public class MLAdwinDriftLog : Entity<long>
{
    /// <summary>Foreign key to the ML model whose directional accuracy stream was evaluated.</summary>
    public long      MLModelId      { get; set; }

    /// <summary>The traded instrument this drift result applies to (for example, "EURUSD").</summary>
    public string    Symbol         { get; set; } = string.Empty;

    /// <summary>The candle timeframe used by the evaluated model and its prediction history.</summary>
    public Timeframe Timeframe      { get; set; } = Timeframe.H1;

    /// <summary>
    /// <c>true</c> when ADWIN found a statistically significant shift between the older
    /// and newer accuracy windows; otherwise <c>false</c>.
    /// </summary>
    public bool      DriftDetected  { get; set; }

    /// <summary>
    /// Mean directional accuracy of the older ADWIN sub-window at the selected split point.
    /// On healthy rows this still reflects the most informative audited split, which may
    /// show stability, a near-miss, or even a statistically significant improvement.
    /// </summary>
    public double    Window1Mean    { get; set; }

    /// <summary>
    /// Mean directional accuracy of the newer ADWIN sub-window at the selected split point.
    /// On healthy rows this still reflects the most informative audited split, which may
    /// show stability, a near-miss, or even a statistically significant improvement.
    /// </summary>
    public double    Window2Mean    { get; set; }

    /// <summary>
    /// Hoeffding-derived significance threshold for the selected split point. Drift is
    /// declared when <c>Abs(Window1Mean - Window2Mean) &gt; EpsilonCut</c>.
    /// </summary>
    public double    EpsilonCut     { get; set; }

    /// <summary>
    /// Number of older prediction outcomes in the first ADWIN sub-window chosen for the
    /// persisted audit row.
    /// </summary>
    public int       Window1Size    { get; set; }

    /// <summary>
    /// Number of newer prediction outcomes in the second ADWIN sub-window. Together with
    /// <see cref="Window1Size"/>, this equals the monitored prediction window size.
    /// </summary>
    public int       Window2Size    { get; set; }

    /// <summary>UTC timestamp when the ADWIN scan result was recorded.</summary>
    public DateTime  DetectedAt     { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The accuracy drop (Window1Mean − Window2Mean), clamped to ≥ 0 when drift was detected.
    /// Persisted so the operations team can chart degradation magnitude over time and tune
    /// <c>Delta</c> from the historical false-positive distribution. <c>0</c> on healthy
    /// (non-drift) audit rows.
    /// </summary>
    public double    AccuracyDrop   { get; set; }

    /// <summary>
    /// The Hoeffding confidence parameter that was active when the scan ran. Stored alongside
    /// each row so retrospective analysis can reconstruct exactly which threshold was used,
    /// even after operators tune <c>MLAdwinDrift:Delta</c>.
    /// </summary>
    public double    DeltaUsed      { get; set; }

    /// <summary>
    /// The dominant <see cref="MarketRegime"/> observed during the scanned window. Captured so
    /// drift can be triaged by regime context (e.g. distinguishing model degradation from a
    /// regime transition that the model is correctly tracking). Null when no
    /// <c>MarketRegimeSnapshot</c> was available in the lookback window.
    /// </summary>
    public MarketRegime? DominantRegime { get; set; }

    /// <summary>
    /// Compressed (gzip) byte array of the binary outcome series used by the detector. One
    /// byte per outcome (1 = direction correct, 0 = wrong). Enables forensic replay of the
    /// detection decision long after the source <c>MLModelPredictionLog</c> rows have been
    /// pruned. Null on rows where snapshotting is disabled by configuration.
    /// </summary>
    public byte[]?   OutcomeSeriesCompressed { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool      IsDeleted      { get; set; }

    // Navigation properties

    /// <summary>The ML model whose prediction stream produced this drift log.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
