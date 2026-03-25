using System.Text.Json;
using System.Text.Json.Serialization;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Captures before/after state of an entity for audit trail entries.
/// Serializes to JSON suitable for <see cref="Domain.Entities.DecisionLog.ContextJson"/>.
/// </summary>
public static class AuditSnapshot
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Creates a JSON string containing before and after snapshots of entity state.
    /// Use anonymous types to select only the fields relevant to the change.
    /// </summary>
    /// <example>
    /// AuditSnapshot.Capture(
    ///     before: new { profile.MaxPositionSize, profile.MaxDailyLoss },
    ///     after:  new { request.MaxPositionSize, request.MaxDailyLoss });
    /// </example>
    public static string Capture(object? before, object? after)
    {
        return JsonSerializer.Serialize(new { before, after }, SerializerOptions);
    }

    /// <summary>
    /// Creates a JSON string containing only the "after" state (for creation events).
    /// </summary>
    public static string CaptureCreated(object after)
    {
        return JsonSerializer.Serialize(new { created = after }, SerializerOptions);
    }
}
