using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Fitted Hawkes process kernel parameters for self-exciting signal arrival modelling
/// per symbol/timeframe (Rec #32).
/// </summary>
/// <remarks>
/// A Hawkes process models the intensity of signal arrivals as:
///   λ(t) = μ + α × Σ_i exp(−β × (t − t_i)) for all past arrivals t_i &lt; t
/// where μ is the baseline rate, α the excitation amplitude, and β the decay rate.
/// When λ(t) is elevated (due to recent signal clustering), new signals are suppressed
/// until the intensity decays back toward μ.  <c>MLHawkesProcessWorker</c> fits these
/// parameters daily using maximum likelihood estimation on recent signal timestamps
/// and writes the result here. <c>HawkesSignalFilter</c> reads the latest row to
/// evaluate whether a new signal arrives during a burst episode.
/// </remarks>
public class MLHawkesKernelParams : Entity<long>
{
    /// <summary>The currency pair (e.g. "EURUSD").</summary>
    public string    Symbol          { get; set; } = string.Empty;

    /// <summary>The chart timeframe.</summary>
    public Timeframe Timeframe       { get; set; } = Timeframe.H1;

    /// <summary>
    /// Baseline signal arrival rate (events per hour) in the absence of self-excitation.
    /// </summary>
    public double    Mu              { get; set; }

    /// <summary>
    /// Excitation amplitude.  Each past arrival raises the intensity by α at t=t_i,
    /// then decays exponentially.  Values in (0, β) guarantee stationarity.
    /// </summary>
    public double    Alpha           { get; set; }

    /// <summary>Exponential decay rate (per hour). Higher β = shorter memory.</summary>
    public double    Beta            { get; set; }

    /// <summary>
    /// Log-likelihood of the Hawkes model on the calibration window.
    /// Used to assess goodness-of-fit; lower (more negative) is worse.
    /// </summary>
    public double?   LogLikelihood   { get; set; }

    /// <summary>
    /// Intensity suppression threshold.  When the current λ(t) exceeds
    /// <see cref="Mu"/> × <see cref="SuppressMultiplier"/>, new signals are suppressed.
    /// Default 2.0 (suppress when intensity is 2× the baseline rate).
    /// </summary>
    public double    SuppressMultiplier { get; set; } = 2.0;

    /// <summary>Number of signal events used to fit the kernel.</summary>
    public int       FitSamples      { get; set; }

    /// <summary>UTC timestamp when this fit was produced.</summary>
    public DateTime  FittedAt        { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag.</summary>
    public bool      IsDeleted       { get; set; }
}
