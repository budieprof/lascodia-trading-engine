using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores a Temperature Scaling post-hoc calibration snapshot for a given model, symbol,
/// and timeframe. Records the optimal temperature found and the improvement in calibration
/// metrics before and after scaling.
/// </summary>
public class MLTemperatureScalingLog : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> this log belongs to.</summary>
    public long MLModelId { get; set; }

    /// <summary>The currency pair this log covers (e.g. "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The chart timeframe this log covers (e.g. "H1").</summary>
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Optimal temperature scalar T* found by minimising NLL on the validation set.</summary>
    public double OptimalTemperature { get; set; }

    /// <summary>Expected Calibration Error (ECE) before temperature scaling.</summary>
    public double PreCalibrationEce { get; set; }

    /// <summary>Expected Calibration Error (ECE) after temperature scaling.</summary>
    public double PostCalibrationEce { get; set; }

    /// <summary>Negative log-likelihood (NLL) before temperature scaling.</summary>
    public double PreCalibrationNll { get; set; }

    /// <summary>Negative log-likelihood (NLL) after temperature scaling.</summary>
    public double PostCalibrationNll { get; set; }

    /// <summary>Number of validation samples used to find the optimal temperature.</summary>
    public int CalibrationSamples { get; set; }

    /// <summary>UTC timestamp when this snapshot was computed.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
