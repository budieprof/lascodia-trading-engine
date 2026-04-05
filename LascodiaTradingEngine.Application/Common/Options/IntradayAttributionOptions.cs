using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configuration for the intraday performance attribution worker.
/// Bound from the <c>IntradayAttributionOptions</c> section in appsettings.json.
/// </summary>
public class IntradayAttributionOptions : ConfigurationOption<IntradayAttributionOptions>
{
    /// <summary>Whether intraday performance attribution is enabled. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Polling interval in seconds for the attribution worker. Defaults to 3600 (1 hour).</summary>
    public int PollIntervalSeconds { get; set; } = 3600;
}
