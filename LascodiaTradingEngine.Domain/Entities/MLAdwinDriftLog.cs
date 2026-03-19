using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>ADWIN adaptive windowing drift detection log (Rec #140).</summary>
public class MLAdwinDriftLog : Entity<long>
{
    public long      MLModelId      { get; set; }
    public string    Symbol         { get; set; } = string.Empty;
    public Timeframe Timeframe      { get; set; } = Timeframe.H1;
    public bool      DriftDetected  { get; set; }
    public double    Window1Mean    { get; set; }
    public double    Window2Mean    { get; set; }
    public double    EpsilonCut     { get; set; }
    public int       Window1Size    { get; set; }
    public int       Window2Size    { get; set; }
    public DateTime  DetectedAt     { get; set; } = DateTime.UtcNow;
    public bool      IsDeleted      { get; set; }

    public virtual MLModel MLModel { get; set; } = null!;
}
