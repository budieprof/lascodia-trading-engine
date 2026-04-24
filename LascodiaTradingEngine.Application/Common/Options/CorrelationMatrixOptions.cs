using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the rolling correlation-matrix worker used by risk checks.</summary>
public class CorrelationMatrixOptions : ConfigurationOption<CorrelationMatrixOptions>
{
    /// <summary>How often the worker recomputes the published matrix.</summary>
    public int PollIntervalHours { get; set; } = 6;

    /// <summary>Calendar-day lookback window used to load closed D1 candles.</summary>
    public int LookbackDays { get; set; } = 60;

    /// <summary>Minimum number of normalized daily closes a symbol must have before it is eligible.</summary>
    public int MinClosesPerSymbol { get; set; } = 20;

    /// <summary>Minimum aligned daily return observations required to publish a pair correlation.</summary>
    public int MinOverlapPoints { get; set; } = 15;

    /// <summary>Exponential half-life, in observations, used by the weighted Pearson calculation.</summary>
    public double DecayHalfLife { get; set; } = 20.0;
}
