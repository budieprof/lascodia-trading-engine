namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Thread-safe registry of active TCP bridge sessions.
/// Maps each session to its owning trading account and a push callback.
/// </summary>
public interface ITcpBridgeSessionRegistry
{
    /// <summary>
    /// Register a new bridge session.
    /// </summary>
    /// <param name="sessionId">Unique session identifier (e.g. GUID).</param>
    /// <param name="accountId">Engine TradingAccount.Id extracted from the JWT.</param>
    /// <param name="push">Callback to deliver a JSON message line to this session's socket.</param>
    void RegisterSession(string sessionId, long accountId, Func<string, Task> push);

    /// <summary>Remove a session on disconnect.</summary>
    void UnregisterSession(string sessionId);

    /// <summary>
    /// Push a JSON message to every active session belonging to <paramref name="accountId"/>.
    /// Delivery is best-effort; a broken session callback should not throw.
    /// </summary>
    Task PushToAccountAsync(long accountId, string messageJson, CancellationToken ct = default);

    /// <summary>Number of currently registered sessions for a given account.</summary>
    int SessionCountForAccount(long accountId);

    /// <summary>Total number of registered sessions across all accounts.</summary>
    int TotalSessionCount { get; }

    /// <summary>Associates a session with an EA instance ID for command routing.</summary>
    void RegisterInstanceMapping(string sessionId, string instanceId);

    /// <summary>Pushes a message to all sessions for a specific EA instance.</summary>
    Task PushToInstanceAsync(string instanceId, string messageJson, CancellationToken ct = default);

    /// <summary>Gets all unique instance IDs with active sessions.</summary>
    IReadOnlyList<string> GetConnectedInstanceIds();
}
