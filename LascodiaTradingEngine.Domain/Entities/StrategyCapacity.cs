using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Estimated capital capacity for a strategy before returns degrade due to market impact.
/// Computed by the StrategyCapacityWorker using historical volume participation rates
/// and calibrated market impact curves from execution quality data.
/// </summary>
public class StrategyCapacity : Entity<long>
{
    /// <summary>FK to the strategy.</summary>
    public long StrategyId { get; set; }

    /// <summary>Symbol this capacity estimate applies to.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Average daily volume (ADV) for the symbol in lots over the estimation window.</summary>
    public decimal AverageDailyVolume { get; set; }

    /// <summary>Strategy's average daily volume as percentage of symbol's ADV.</summary>
    public decimal VolumeParticipationRatePct { get; set; }

    /// <summary>Maximum lot size before expected market impact exceeds expected alpha.</summary>
    public decimal CapacityCeilingLots { get; set; }

    /// <summary>Current aggregate lot size across all accounts using this strategy.</summary>
    public decimal CurrentAggregateLots { get; set; }

    /// <summary>Capacity utilisation (CurrentAggregateLots / CapacityCeilingLots).</summary>
    public decimal UtilizationPct { get; set; }

    /// <summary>
    /// JSON-serialised market impact curve: [{ "lots": 0.1, "expectedSlippagePips": 0.2 }, ...]
    /// Calibrated from ExecutionQualityLog data using power-law regression.
    /// </summary>
    public string MarketImpactCurveJson { get; set; } = "[]";

    /// <summary>Estimated slippage in pips at current aggregate lot size.</summary>
    public decimal EstimatedSlippageAtCurrentSize { get; set; }

    /// <summary>Number of days of execution data used to calibrate the impact curve.</summary>
    public int CalibrationWindowDays { get; set; }

    /// <summary>When this capacity estimate was last computed.</summary>
    public DateTime EstimatedAt { get; set; } = DateTime.UtcNow;

    public virtual Strategy Strategy { get; set; } = null!;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
