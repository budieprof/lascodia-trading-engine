using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores a single runtime configuration key-value pair for the trading engine.
/// Provides a database-backed, hot-reloadable alternative to <c>appsettings.json</c>
/// for settings that need to be changed without restarting the service.
/// </summary>
/// <remarks>
/// Configuration entries follow a hierarchical dot-notation key scheme mirroring
/// the <c>appsettings.json</c> structure (e.g. "RiskMonitor:IntervalSeconds",
/// "NewsFilter:HaltMinutesBefore"). The engine's configuration provider polls for
/// changes to <see cref="IsHotReloadable"/> entries and applies them at runtime.
///
/// Type-safe access is provided via the <c>IEngineConfigService</c> which reads the
/// <see cref="DataType"/> property to deserialise the raw string <see cref="Value"/>
/// into the appropriate CLR type (int, decimal, bool, string, JSON).
/// </remarks>
public class EngineConfig : Entity<long>
{
    /// <summary>
    /// Hierarchical dot-notation key for this setting.
    /// e.g. "RiskMonitor:IntervalSeconds", "StrategyHealth:CriticalThreshold",
    /// "NewsFilter:HaltMinutesBefore".
    /// Must be unique across all non-deleted config entries.
    /// </summary>
    public string  Key             { get; set; } = string.Empty;

    /// <summary>
    /// The raw string value for this setting. Parsed according to <see cref="DataType"/>
    /// by the configuration service. e.g. "30", "0.3", "true", or a JSON object string.
    /// </summary>
    public string  Value           { get; set; } = string.Empty;

    /// <summary>
    /// Optional human-readable description explaining what this setting controls,
    /// its valid range, and the impact of changing it.
    /// </summary>
    public string? Description     { get; set; }

    /// <summary>
    /// The CLR data type of <see cref="Value"/>: String, Integer, Decimal, Boolean, or Json.
    /// Used by the configuration service to deserialise the value safely.
    /// </summary>
    public ConfigDataType  DataType        { get; set; } = ConfigDataType.String;

    /// <summary>
    /// When <c>true</c>, the configuration provider monitors this entry and applies changes
    /// at runtime without a service restart. When <c>false</c>, a restart is required to
    /// pick up the new value.
    /// </summary>
    public bool    IsHotReloadable { get; set; } = true;

    /// <summary>UTC timestamp of the most recent update to this configuration entry.</summary>
    public DateTime LastUpdatedAt  { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted       { get; set; }
}
