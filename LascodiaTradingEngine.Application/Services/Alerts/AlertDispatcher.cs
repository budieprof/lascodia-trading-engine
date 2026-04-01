using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Alerts;

/// <summary>
/// Routes a triggered alert to the appropriate <see cref="IAlertChannelSender"/>
/// based on <see cref="Alert.Channel"/> or severity-based routing.
/// Singleton lifetime to hold deduplication state across requests.
/// Uses IServiceScopeFactory to resolve scoped senders on each dispatch.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IAlertDispatcher))]
public class AlertDispatcher : IAlertDispatcher, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlertDispatcher> _logger;

    /// <summary>Deduplication cache: DeduplicationKey -> last dispatch UTC timestamp.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _dedup = new();

    /// <summary>Periodic timer that evicts stale dedup entries even when no alerts are dispatched.</summary>
    private readonly Timer _evictionTimer;

    public AlertDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<AlertDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;

        // Run eviction every 30 minutes regardless of dispatch activity
        _evictionTimer = new Timer(_ =>
        {
            try { EvictStaleDedupEntries(force: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "AlertDispatcher: dedup eviction failed"); }
        }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <remarks>
    /// Sets <see cref="Alert.LastTriggeredAt"/> on the entity on successful dispatch.
    /// The caller is responsible for calling SaveChangesAsync afterwards,
    /// as the dispatcher (Singleton) does not own a DbContext.
    /// </remarks>
    public async Task DispatchAsync(Alert alert, string message, CancellationToken ct)
    {
        if (IsDeduplicated(alert))
            return;

        using var scope = _scopeFactory.CreateScope();
        var senders = scope.ServiceProvider.GetServices<IAlertChannelSender>()
            .ToDictionary(s => s.Channel);

        if (senders.TryGetValue(alert.Channel, out var sender))
        {
            await sender.SendAsync(alert, message, ct);
            RecordDispatch(alert);
        }
        else
        {
            _logger.LogWarning(
                "AlertDispatcher: no sender registered for channel {Channel} (alert {AlertId})",
                alert.Channel, alert.Id);
        }
    }

    /// <remarks>
    /// Sets <see cref="Alert.LastTriggeredAt"/> on the entity on successful dispatch.
    /// The caller is responsible for calling SaveChangesAsync afterwards,
    /// as the dispatcher (Singleton) does not own a DbContext.
    /// </remarks>
    public async Task DispatchBySeverityAsync(Alert alert, string message, CancellationToken ct)
    {
        if (IsDeduplicated(alert))
            return;

        using var scope = _scopeFactory.CreateScope();
        var senders = scope.ServiceProvider.GetServices<IAlertChannelSender>()
            .ToDictionary(s => s.Channel);

        var channels = GetChannelsForSeverity(alert.Severity);
        bool anySent = false;

        foreach (var channel in channels)
        {
            if (senders.TryGetValue(channel, out var sender))
            {
                try
                {
                    await sender.SendAsync(alert, message, ct);
                    anySent = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "AlertDispatcher: failed to send alert {AlertId} via {Channel} (other channels may have succeeded)",
                        alert.Id, channel);
                }
            }
            else
            {
                _logger.LogDebug(
                    "AlertDispatcher: no sender registered for channel {Channel} (alert {AlertId}, severity {Severity})",
                    channel, alert.Id, alert.Severity);
            }
        }

        if (anySent)
            RecordDispatch(alert);
    }

    public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
    {
        if (conditionStillActive || alert.AutoResolvedAt.HasValue)
            return Task.CompletedTask;

        alert.AutoResolvedAt = DateTime.UtcNow;

        var resolvedMessage = $"[RESOLVED] Alert {alert.Id} ({alert.AlertType} on {alert.Symbol}) has auto-resolved.";

        _logger.LogInformation(
            "Alert {AlertId} auto-resolved at {ResolvedAt}",
            alert.Id, alert.AutoResolvedAt);

        // Clear the dedup entry so the resolved notification can go through
        if (!string.IsNullOrEmpty(alert.DeduplicationKey))
            _dedup.TryRemove(alert.DeduplicationKey, out _);

        return DispatchBySeverityAsync(alert, resolvedMessage, ct);
    }

    /// <summary>
    /// Maps severity to the set of channels to dispatch to.
    /// Critical/High -> Telegram + Webhook; Medium -> Webhook; Info -> Webhook.
    /// </summary>
    private static AlertChannel[] GetChannelsForSeverity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => [AlertChannel.Telegram, AlertChannel.Webhook],
        AlertSeverity.High     => [AlertChannel.Telegram, AlertChannel.Webhook],
        AlertSeverity.Medium   => [AlertChannel.Webhook],
        AlertSeverity.Info     => [AlertChannel.Webhook],
        _                      => [AlertChannel.Webhook],
    };

    /// <summary>
    /// Checks whether this alert should be suppressed based on deduplication key and cooldown.
    /// Returns true if the alert should NOT be dispatched (is a duplicate within cooldown).
    /// </summary>
    private bool IsDeduplicated(Alert alert)
    {
        if (string.IsNullOrEmpty(alert.DeduplicationKey))
            return false;

        if (_dedup.TryGetValue(alert.DeduplicationKey, out var lastDispatch))
        {
            var elapsed = (DateTime.UtcNow - lastDispatch).TotalSeconds;
            if (elapsed < alert.CooldownSeconds)
            {
                _logger.LogDebug(
                    "AlertDispatcher: suppressing alert {AlertId} (dedup key {Key}, {Elapsed:F0}s/{Cooldown}s)",
                    alert.Id, alert.DeduplicationKey, elapsed, alert.CooldownSeconds);
                return true;
            }
        }

        return false;
    }

    /// <summary>Records a successful dispatch for dedup tracking.</summary>
    private void RecordDispatch(Alert alert)
    {
        alert.LastTriggeredAt = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(alert.DeduplicationKey))
        {
            _dedup[alert.DeduplicationKey] = DateTime.UtcNow;
            EvictStaleDedupEntries();
        }
    }

    /// <summary>
    /// Evicts dedup entries older than 1 hour. When <paramref name="force"/> is false
    /// (dispatch-time call), eviction only runs if the cache exceeds 10k entries.
    /// When true (timer-based call), eviction always runs.
    /// </summary>
    private void EvictStaleDedupEntries(bool force = false)
    {
        if (!force && _dedup.Count <= 10_000)
            return;

        var cutoff = DateTime.UtcNow.AddHours(-1);
        var snapshot = _dedup.ToArray();
        foreach (var (key, ts) in snapshot)
        {
            if (ts < cutoff)
                _dedup.TryRemove(key, out _);
        }
    }
}
