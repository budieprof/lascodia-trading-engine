using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persists trading session schedule data received from EA instances.
/// Each record represents a single session window (e.g. "London" for "EURUSD")
/// with UTC open/close times and day-of-week bounds.
/// </summary>
public class TradingSessionSchedule : Entity<long>
{
    /// <summary>Instrument symbol this session applies to.</summary>
    public required string Symbol { get; set; }

    /// <summary>Session name (e.g. "London", "NewYork", "Tokyo", "Sydney").</summary>
    public required string SessionName { get; set; }

    /// <summary>Session open time (time of day, UTC).</summary>
    public TimeSpan OpenTime { get; set; }

    /// <summary>Session close time (time of day, UTC).</summary>
    public TimeSpan CloseTime { get; set; }

    /// <summary>Day of week the session starts (0 = Sunday, 1 = Monday, etc.).</summary>
    public int DayOfWeekStart { get; set; }

    /// <summary>Day of week the session ends.</summary>
    public int DayOfWeekEnd { get; set; }

    /// <summary>EA instance that reported this session schedule.</summary>
    public string? InstanceId { get; set; }

    /// <summary>UTC timestamp when this record was first created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
