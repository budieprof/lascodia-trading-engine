using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configuration for chaos testing. MUST remain disabled in production.
/// When enabled, randomly injects failures and latency into the MediatR pipeline.
/// </summary>
public class ChaosTestingOptions : ConfigurationOption<ChaosTestingOptions>
{
    /// <summary>Master switch. Must be explicitly set to true to enable.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Percentage of requests that will fail with a random exception (0-100).</summary>
    public int FailureRatePct { get; set; } = 5;

    /// <summary>Maximum random latency to inject in milliseconds.</summary>
    public int MaxLatencyInjectionMs { get; set; } = 500;

    /// <summary>Command type names that chaos testing applies to. Empty = all commands.</summary>
    public List<string> AffectedCommands { get; set; } = new();
}
