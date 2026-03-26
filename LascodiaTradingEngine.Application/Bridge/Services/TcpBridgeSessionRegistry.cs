using System.Collections.Concurrent;
using LascodiaTradingEngine.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Bridge.Services;

/// <summary>
/// In-memory, thread-safe implementation of <see cref="ITcpBridgeSessionRegistry"/>.
/// Registered as a singleton in DI so <see cref="Workers.TcpBridgeWorker"/> and
/// the shared signal poller share the same registry state.
/// </summary>
public sealed class TcpBridgeSessionRegistry : ITcpBridgeSessionRegistry
{
    private sealed record SessionEntry(long AccountId, Func<string, Task> Push);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ILogger<TcpBridgeSessionRegistry> _logger;

    public TcpBridgeSessionRegistry(ILogger<TcpBridgeSessionRegistry> logger)
        => _logger = logger;

    public void RegisterSession(string sessionId, long accountId, Func<string, Task> push)
    {
        _sessions[sessionId] = new SessionEntry(accountId, push);
        _logger.LogDebug("Bridge: session {SessionId} registered for account {AccountId} (total={Total})",
            sessionId, accountId, _sessions.Count);
    }

    public void UnregisterSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
            _logger.LogDebug("Bridge: session {SessionId} (account={AccountId}) unregistered (total={Total})",
                sessionId, entry.AccountId, _sessions.Count);
    }

    public async Task PushToAccountAsync(long accountId, string messageJson, CancellationToken ct = default)
    {
        foreach (var (sessionId, entry) in _sessions)
        {
            if (entry.AccountId != accountId) continue;
            try
            {
                await entry.Push(messageJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Bridge: push to session {SessionId} (account={AccountId}) failed",
                    sessionId, accountId);
            }
        }
    }

    public int SessionCountForAccount(long accountId)
        => _sessions.Values.Count(e => e.AccountId == accountId);

    public int TotalSessionCount => _sessions.Count;
}
