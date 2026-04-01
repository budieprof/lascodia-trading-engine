using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the degradation mode manager and auto-degradation thresholds.</summary>
public class DegradationOptions : ConfigurationOption<DegradationOptions>
{
    /// <summary>Seconds without ML scorer heartbeat before transitioning to MLDegraded.</summary>
    public int MLScorerStalenessSeconds { get; set; } = 120;

    /// <summary>Seconds without event bus heartbeat before transitioning to EventBusDegraded.</summary>
    public int EventBusStalenessSeconds { get; set; } = 60;

    /// <summary>Seconds without read DB heartbeat before transitioning to ReadDbDegraded.</summary>
    public int ReadDbStalenessSeconds { get; set; } = 30;

    /// <summary>Lot size multiplier when in MLDegraded mode (e.g., 0.5 = 50% reduction).</summary>
    public decimal MLDegradedLotSizeMultiplier { get; set; } = 0.5m;
}
