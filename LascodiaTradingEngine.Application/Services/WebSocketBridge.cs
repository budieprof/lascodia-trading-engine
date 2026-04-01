using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Manages WebSocket connections to EA instances for push-based command delivery.
/// Thread-safe: connections are tracked in a ConcurrentDictionary.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class WebSocketBridge : IWebSocketBridge
{
    private readonly WebSocketBridgeOptions _options;
    private readonly ILogger<WebSocketBridge> _logger;
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public WebSocketBridge(
        WebSocketBridgeOptions options,
        ILogger<WebSocketBridge> logger)
    {
        _options = options;
        _logger  = logger;
    }

    public bool IsConnected(string instanceId)
        => _connections.TryGetValue(instanceId, out var ws) && ws.State == WebSocketState.Open;

    public async Task<bool> PushCommandAsync(string instanceId, EACommand command, CancellationToken ct)
    {
        if (!_options.Enabled)
            return false;

        if (!_connections.TryGetValue(instanceId, out var ws) || ws.State != WebSocketState.Open)
            return false;

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                command.Id,
                command.CommandType,
                command.Symbol,
                command.TargetTicket,
                command.Parameters
            });

            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            _logger.LogDebug("WebSocket: pushed command {Id} to {Instance}", command.Id, instanceId);
            return true;
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket: failed to push to {Instance}, removing connection", instanceId);
            UnregisterConnection(instanceId);
            return false;
        }
    }

    public void RegisterConnection(string instanceId, WebSocket socket)
    {
        if (_connections.Count >= _options.MaxConnections)
        {
            _logger.LogWarning("WebSocket: max connections ({Max}) reached, rejecting {Instance}",
                _options.MaxConnections, instanceId);
            return;
        }

        _connections[instanceId] = socket;
        _logger.LogInformation("WebSocket: registered connection for {Instance}", instanceId);
    }

    public void UnregisterConnection(string instanceId)
    {
        if (_connections.TryRemove(instanceId, out var ws))
        {
            try { ws.Dispose(); } catch { /* best-effort cleanup */ }
            _logger.LogInformation("WebSocket: unregistered connection for {Instance}", instanceId);
        }
    }
}
