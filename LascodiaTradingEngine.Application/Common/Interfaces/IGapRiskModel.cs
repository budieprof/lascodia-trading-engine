namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Computes symbol-specific gap risk multipliers from historical Monday open vs Friday close data.
/// Replaces the static 1.5x weekend gap multiplier in RiskChecker.
/// </summary>
public record GapRiskEstimate(
    decimal GapMultiplier,
    decimal P99GapPct,
    int SampleCount,
    DateTime LastCalibrated);

public interface IGapRiskModel
{
    /// <summary>Returns the gap risk multiplier for the given symbol based on historical weekend/holiday gaps.</summary>
    Task<GapRiskEstimate> GetGapMultiplierAsync(
        string symbol,
        CancellationToken cancellationToken);

    /// <summary>Recalibrates all symbol gap multipliers from historical open/close data.</summary>
    Task RecalibrateAsync(CancellationToken cancellationToken);
}
