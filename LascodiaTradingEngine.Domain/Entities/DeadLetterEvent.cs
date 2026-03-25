using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores integration events that exhausted all retry attempts in their event handler.
/// Allows manual inspection and replay instead of silently dropping events.
/// </summary>
public class DeadLetterEvent : Entity<long>
{
    /// <summary>Name of the event handler that failed (e.g. "OrderFilledEventHandler").</summary>
    public string HandlerName { get; set; } = string.Empty;

    /// <summary>Type name of the integration event (e.g. "OrderFilledIntegrationEvent").</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>JSON-serialized event payload for replay.</summary>
    public string EventPayload { get; set; } = string.Empty;

    /// <summary>The exception message from the final failed attempt.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Full stack trace from the final failed attempt.</summary>
    public string? StackTrace { get; set; }

    /// <summary>Number of attempts made before dead-lettering.</summary>
    public int Attempts { get; set; }

    /// <summary>UTC timestamp when the event was dead-lettered.</summary>
    public DateTime DeadLetteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this event has been manually resolved/replayed.</summary>
    public bool IsResolved { get; set; }

    /// <summary>Soft-delete flag.</summary>
    public bool IsDeleted { get; set; }
}
