namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Lifecycle status of a registered EA instance.
/// </summary>
public enum EAInstanceStatus
{
    /// <summary>The EA is actively connected and sending heartbeats.</summary>
    Active,

    /// <summary>The EA has missed heartbeats and is considered disconnected.</summary>
    Disconnected,

    /// <summary>The EA is gracefully shutting down after a deregister request.</summary>
    ShuttingDown
}
