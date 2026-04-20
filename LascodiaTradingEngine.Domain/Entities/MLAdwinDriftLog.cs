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
    /// When no drift is found, the current worker leaves this at <c>0</c>.
    /// </summary>
    public double    Window1Mean    { get; set; }

    /// <summary>
    /// Mean directional accuracy of the newer ADWIN sub-window at the selected split point.
    /// When no drift is found, the current worker leaves this at <c>0</c>.
    /// </summary>
    public double    Window2Mean    { get; set; }

    /// <summary>
    /// Hoeffding-derived significance threshold for the selected split point. Drift is
    /// declared when <c>Abs(Window1Mean - Window2Mean) &gt; EpsilonCut</c>.
    /// </summary>
    public double    EpsilonCut     { get; set; }

    /// <summary>
    /// Number of older prediction outcomes in the first ADWIN sub-window. On non-drift
    /// rows, this is the midpoint fallback used for audit consistency.
    /// </summary>
    public int       Window1Size    { get; set; }

    /// <summary>
    /// Number of newer prediction outcomes in the second ADWIN sub-window. Together with
    /// <see cref="Window1Size"/>, this equals the monitored prediction window size.
    /// </summary>
    public int       Window2Size    { get; set; }

    /// <summary>UTC timestamp when the ADWIN scan result was recorded.</summary>
    public DateTime  DetectedAt     { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool      IsDeleted      { get; set; }

    // Navigation properties

    /// <summary>The ML model whose prediction stream produced this drift log.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
