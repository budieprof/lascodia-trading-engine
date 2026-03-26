using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Bridge.Options;

/// <summary>
/// Configuration for the TCP bridge server that EA instances connect to.
/// Bound from the <c>BridgeOptions</c> section in appsettings.json.
/// Overrides via environment variables: BRIDGE_ENABLED, BRIDGE_BIND_ADDRESS,
/// BRIDGE_PORT, BRIDGE_ADVERTISED_HOST, BRIDGE_USE_TLS.
/// </summary>
public class BridgeOptions : ConfigurationOption<BridgeOptions>
{
    /// <summary>Whether the TCP bridge listener is active. Default: false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Address the listener binds to. Use "0.0.0.0" (or "[::]" for IPv6) to accept
    /// connections on all interfaces. Default: "127.0.0.1".
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>TCP port the listener binds to. Default: 5082.</summary>
    public int Port { get; set; } = 5082;

    /// <summary>
    /// Hostname or IP returned to EA clients in the auth response so they know
    /// where to connect. Separate from <see cref="BindAddress"/> so the engine can
    /// bind on 0.0.0.0 while advertising a routable hostname to clients.
    /// Override via env: BRIDGE_ADVERTISED_HOST.
    /// Default: empty — falls back to BindAddress.
    /// </summary>
    public string AdvertisedHost { get; set; } = string.Empty;

    /// <summary>Whether TLS is required on bridge connections. Default: false (plaintext).</summary>
    public bool UseTls { get; set; } = false;

    /// <summary>OS TCP accept-queue depth (SO_BACKLOG). Default: 128.</summary>
    public int TcpBacklog { get; set; } = 128;

    /// <summary>
    /// Hard cap on total simultaneous bridge connections across all accounts.
    /// Excess connections are rejected at accept time. Default: 200.
    /// </summary>
    public int MaxTotalConnections { get; set; } = 200;

    /// <summary>
    /// Per-account connection limit. A second EA connecting for the same account
    /// (e.g. multi-chart same terminal) is allowed up to this count. Default: 10.
    /// </summary>
    public int MaxConnectionsPerAccount { get; set; } = 10;

    /// <summary>
    /// The effective host advertised to EA clients — AdvertisedHost when set,
    /// otherwise BindAddress (replacing "0.0.0.0"/"[::]" with "127.0.0.1").
    /// </summary>
    public string EffectiveAdvertisedHost
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AdvertisedHost))
                return AdvertisedHost;
            // BindAddress on 0.0.0.0 is not routable; fall back to localhost
            return BindAddress is "0.0.0.0" or "[::]" ? "127.0.0.1" : BindAddress;
        }
    }
}
