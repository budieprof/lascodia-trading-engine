using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the WebSocket bridge to EA instances.</summary>
public class WebSocketBridgeOptions : ConfigurationOption<WebSocketBridgeOptions>
{
    /// <summary>Whether WebSocket push is enabled. When false, EA uses HTTP polling only.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Maximum concurrent WebSocket connections.</summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>WebSocket keep-alive interval in seconds.</summary>
    public int KeepAliveIntervalSeconds { get; set; } = 30;
}
