namespace LascodiaTradingEngine.Application.Common.Diagnostics;

/// <summary>
/// Canonical names for latency-SLA pipeline segments.
/// </summary>
public static class LatencySlaSegments
{
    public const string TickToSignal = "TickToSignal";
    public const string SignalToTier1 = "SignalToTier1";
    public const string Tier2RiskCheck = "Tier2RiskCheck";
    public const string EaPollToSubmit = "EaPollToSubmit";
    public const string TotalTickToFill = "TotalTickToFill";
}
