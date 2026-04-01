using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Push-based command delivery to EA instances via WebSocket.
/// Falls back to poll-based delivery if the EA is not connected.
/// </summary>
public interface IWebSocketBridge
{
    /// <summary>Whether a specific EA instance has an active WebSocket connection.</summary>
    bool IsConnected(string instanceId);

    /// <summary>Pushes a command to the EA instance. Returns false if not connected.</summary>
    Task<bool> PushCommandAsync(string instanceId, EACommand command, CancellationToken ct);

    /// <summary>Registers a WebSocket connection for an EA instance.</summary>
    void RegisterConnection(string instanceId, System.Net.WebSockets.WebSocket socket);

    /// <summary>Removes a disconnected EA instance.</summary>
    void UnregisterConnection(string instanceId);
}
