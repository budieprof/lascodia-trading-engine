using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Default <see cref="IExternalServiceCircuitBreaker"/>. Tracks consecutive
/// failure counts per service key; opens the circuit when the count crosses
/// <see cref="FailureThreshold"/>; transitions to half-open after
/// <see cref="OpenDuration"/>; a single successful probe re-closes.
///
/// <para>
/// All operations are lock-free / CAS-based on a per-key <see cref="Entry"/>
/// record so contention from the hot tick path stays minimal. Per-service
/// thresholds or open durations are not currently configurable — a single
/// global policy keeps behaviour predictable. Callers that need different
/// policies for different services can request the cap via the key (e.g.
/// prefix with severity).
/// </para>
///
/// <para>
/// Every state transition emits the <c>trading.circuit_breaker.transitions</c>
/// counter tagged with the service key and the target state, so dashboards can
/// alert on a service that keeps flapping.
/// </para>
/// </summary>
[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton, typeof(IExternalServiceCircuitBreaker))]
public sealed class ExternalServiceCircuitBreaker : IExternalServiceCircuitBreaker
{
    public const int FailureThreshold = 5;
    public static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _timeProvider;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<ExternalServiceCircuitBreaker> _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public ExternalServiceCircuitBreaker(
        TimeProvider timeProvider,
        TradingMetrics metrics,
        ILogger<ExternalServiceCircuitBreaker> logger)
    {
        _timeProvider = timeProvider;
        _metrics = metrics;
        _logger = logger;
    }

    public bool IsOpen(string service)
    {
        if (!_entries.TryGetValue(service, out var entry)) return false;

        if (entry.State == CircuitState.Closed) return false;

        // Open → HalfOpen once the open-duration elapses.
        if (entry.State == CircuitState.Open
            && _timeProvider.GetUtcNow().UtcDateTime >= entry.OpenedUntil)
        {
            var transitioned = new Entry(CircuitState.HalfOpen, 0, DateTime.MinValue, entry.OpenedUntil);
            _entries[service] = transitioned;
            EmitTransition(service, CircuitState.HalfOpen);
            return false;
        }

        return entry.State == CircuitState.Open;
    }

    public void RecordSuccess(string service)
    {
        if (!_entries.TryGetValue(service, out var entry)) return;
        if (entry.State == CircuitState.Closed && entry.FailureCount == 0) return;

        var prev = entry.State;
        _entries[service] = new Entry(CircuitState.Closed, 0, DateTime.MinValue, DateTime.MinValue);
        if (prev != CircuitState.Closed)
            EmitTransition(service, CircuitState.Closed);
    }

    public void RecordFailure(string service)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        _entries.AddOrUpdate(service,
            _ => new Entry(CircuitState.Closed, 1, now, DateTime.MinValue),
            (_, current) =>
            {
                // HalfOpen + failure → straight back to Open with a fresh window.
                if (current.State == CircuitState.HalfOpen)
                    return new Entry(CircuitState.Open, current.FailureCount + 1, now, now.Add(OpenDuration));

                int newCount = current.FailureCount + 1;
                if (newCount >= FailureThreshold && current.State != CircuitState.Open)
                    return new Entry(CircuitState.Open, newCount, now, now.Add(OpenDuration));

                return new Entry(current.State, newCount, now, current.OpenedUntil);
            });

        if (_entries.TryGetValue(service, out var updated)
            && updated.State == CircuitState.Open
            && updated.FailureCount == FailureThreshold)
        {
            EmitTransition(service, CircuitState.Open);
        }
    }

    public void ForceOpen(string service, TimeSpan? duration = null)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var window = duration ?? OpenDuration;
        _entries[service] = new Entry(CircuitState.Open, FailureThreshold, now, now.Add(window));
        EmitTransition(service, CircuitState.Open);
        _logger.LogWarning(
            "ExternalServiceCircuitBreaker: '{Service}' forced OPEN for {Duration}", service, window);
    }

    public void Reset(string service)
    {
        _entries[service] = new Entry(CircuitState.Closed, 0, DateTime.MinValue, DateTime.MinValue);
        EmitTransition(service, CircuitState.Closed);
    }

    private void EmitTransition(string service, CircuitState newState)
    {
        _metrics.CircuitBreakerTransitions.Add(1,
            new KeyValuePair<string, object?>("service", service),
            new KeyValuePair<string, object?>("state", newState.ToString()));
        _logger.LogInformation(
            "ExternalServiceCircuitBreaker: '{Service}' → {State}", service, newState);
    }

    private enum CircuitState { Closed, Open, HalfOpen }

    private readonly record struct Entry(
        CircuitState State,
        int FailureCount,
        DateTime LastFailureAt,
        DateTime OpenedUntil);
}
