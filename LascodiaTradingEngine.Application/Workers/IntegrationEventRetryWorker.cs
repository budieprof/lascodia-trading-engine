using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that retries publishing integration events that failed during
/// the initial <c>SaveAndPublish</c> call. This closes the outbox pattern gap: when
/// <see cref="IEventBus.Publish"/> throws after the DB transaction has committed, the
/// event is marked <see cref="EventStateEnum.PublishedFailed"/> in the
/// <c>IntegrationEventLog</c> table and would otherwise remain orphaned.
///
/// <b>Retry policy:</b>
/// <list type="bullet">
///   <item>Polls every 30 seconds for events in <c>PublishedFailed</c> state.</item>
///   <item>Also picks up events stuck in <c>InProgress</c> for longer than 2 minutes
///         (indicates the publishing process crashed mid-flight).</item>
///   <item>Re-publishes each event via <see cref="IEventBus.Publish"/>.</item>
///   <item>On success, marks the event as <c>Published</c>.</item>
///   <item>On failure, increments <c>TimesSent</c>. Events with <c>TimesSent >= 5</c>
///         are logged as critical and skipped (requires manual investigation).</item>
///   <item>Processes up to 50 events per cycle to avoid long-running transactions.</item>
/// </list>
/// </summary>
public class IntegrationEventRetryWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(2);
    /// <summary>
    /// Events marked Published but older than this threshold are considered potentially lost
    /// in transit (broker ACK not confirmed). Re-publishing closes the data-loss window
    /// between the DB commit and the broker confirming receipt. Handlers are idempotent,
    /// so duplicate delivery is safe.
    /// </summary>
    private static readonly TimeSpan StalePublishedThreshold = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 5;
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly IDegradationModeManager _degradationManager;
    private readonly ILogger<IntegrationEventRetryWorker> _logger;
    private readonly TradingMetrics _metrics;

    /// <summary>
    /// Cache of event type names to CLR types, built lazily from loaded assemblies.
    /// Used to deserialize <see cref="IntegrationEventLogEntry.Content"/> back into the
    /// correct <see cref="IntegrationEvent"/> subclass for re-publishing.
    /// </summary>
    private readonly Lazy<Dictionary<string, Type>> _eventTypes;

    public IntegrationEventRetryWorker(
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IDegradationModeManager degradationManager,
        ILogger<IntegrationEventRetryWorker> logger,
        TradingMetrics metrics)
    {
        _scopeFactory         = scopeFactory;
        _eventBus             = eventBus;
        _degradationManager   = degradationManager;
        _logger               = logger;
        _metrics              = metrics;

        _eventTypes = new Lazy<Dictionary<string, Type>>(() =>
        {
            var baseType = typeof(IntegrationEvent);
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
                })
                .Where(t => t is not null && baseType.IsAssignableFrom(t) && !t.IsAbstract)
                .ToDictionary(t => t.FullName!, t => t, StringComparer.OrdinalIgnoreCase);
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IntegrationEventRetryWorker starting (poll interval: {Interval})", PollInterval);

        // Wait briefly for the event bus to finish initializing
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RetryFailedEventsAsync(stoppingToken);
                await RePublishStaleEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IntegrationEventRetryWorker: error during retry cycle");
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "IntegrationEventRetryWorker"));
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("IntegrationEventRetryWorker stopped");
    }

    private async Task RetryFailedEventsAsync(CancellationToken ct)
    {
        // ── Event bus health pre-check: if the bus is in a degraded state, skip
        // publishing entirely. The events remain in the outbox (PublishedFailed /
        // InProgress) and will be retried on the next cycle when the bus recovers.
        // This avoids burning retry attempts against an unhealthy broker connection.
        if (_degradationManager.CurrentMode == DegradationMode.EventBusDegraded)
        {
            _logger.LogWarning(
                "IntegrationEventRetryWorker: event bus is degraded — skipping retry cycle " +
                "(events remain in outbox for next cycle)");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogReader>();

        var failedEvents = await eventLog.GetRetryableEventsAsync(
            StuckThreshold, MaxRetries, BatchSize, ct);

        if (failedEvents.Count == 0) return;

        _logger.LogInformation(
            "IntegrationEventRetryWorker: found {Count} failed/stuck events to retry",
            failedEvents.Count);

        int retried = 0, exhausted = 0;

        foreach (var entry in failedEvents)
        {
            if (ct.IsCancellationRequested) break;

            // ── Terminal state catch-up: if the event already exhausted retries but was
            // never properly transitioned (e.g., process crash between incrementing
            // TimesSent and marking NotPublished), move it to terminal now instead of
            // re-publishing. This prevents stuck events from re-entering the retry loop.
            if (entry.TimesSent >= MaxRetries)
            {
                exhausted++;
                _logger.LogCritical(
                    "IntegrationEventRetryWorker: event {EventId} ({Type}) found in non-terminal state with " +
                    "{Attempts} attempts (>= max {Max}) — transitioning to terminal state",
                    entry.EventId, entry.EventTypeShortName, entry.TimesSent, MaxRetries);

                try
                {
                    var deadLetterSink = scope.ServiceProvider.GetRequiredService<IDeadLetterSink>();
                    await deadLetterSink.WriteAsync(
                        handlerName:      nameof(IntegrationEventRetryWorker),
                        eventType:        entry.EventTypeShortName ?? "Unknown",
                        eventPayloadJson: entry.Content ?? "{}",
                        errorMessage:     $"Stuck event recovered: exhausted {entry.TimesSent} retries without reaching terminal state",
                        stackTrace:       null,
                        attempts:         entry.TimesSent,
                        CancellationToken.None);
                    _metrics.EventRetryDeadLettered.Add(1);
                }
                catch (Exception dlEx)
                {
                    _logger.LogCritical(dlEx,
                        "IntegrationEventRetryWorker: FAILED to dead-letter stuck event {EventId}", entry.EventId);
                }

                entry.State = EventStateEnum.NotPublished;
                await eventLog.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            // Resolve the CLR type for deserialization
            if (!_eventTypes.Value.TryGetValue(entry.EventTypeName, out var eventType))
            {
                _logger.LogWarning(
                    "IntegrationEventRetryWorker: unknown event type {Type} for event {EventId} — skipping",
                    entry.EventTypeName, entry.EventId);
                continue;
            }

            try
            {
                // Deserialize and re-publish
                entry.DeserializeJsonContent(eventType);
                if (entry.IntegrationEvent is null)
                {
                    _logger.LogWarning(
                        "IntegrationEventRetryWorker: failed to deserialize event {EventId} ({Type}) — skipping",
                        entry.EventId, entry.EventTypeShortName);
                    continue;
                }

                entry.State = EventStateEnum.InProgress;
                entry.TimesSent++;
                await eventLog.SaveChangesAsync(ct);

                _eventBus.Publish(entry.IntegrationEvent);

                entry.State = EventStateEnum.Published;
                await eventLog.SaveChangesAsync(ct);

                retried++;
                _logger.LogInformation(
                    "IntegrationEventRetryWorker: successfully re-published event {EventId} ({Type}) on attempt {Attempt}",
                    entry.EventId, entry.EventTypeShortName, entry.TimesSent);
            }
            catch (Exception ex)
            {
                entry.State = EventStateEnum.PublishedFailed;
                await eventLog.SaveChangesAsync(CancellationToken.None);

                if (entry.TimesSent >= MaxRetries)
                {
                    exhausted++;
                    _logger.LogCritical(
                        "IntegrationEventRetryWorker: event {EventId} ({Type}) EXHAUSTED after {Attempts} retries — " +
                        "requires manual investigation. Last error: {Error}",
                        entry.EventId, entry.EventTypeShortName, entry.TimesSent, ex.Message);

                    // Write to dead-letter sink so the event is preserved for manual replay
                    try
                    {
                        var deadLetterSink = scope.ServiceProvider.GetRequiredService<IDeadLetterSink>();
                        await deadLetterSink.WriteAsync(
                            handlerName:      nameof(IntegrationEventRetryWorker),
                            eventType:        entry.EventTypeShortName ?? "Unknown",
                            eventPayloadJson: entry.Content ?? "{}",
                            errorMessage:     $"Exhausted after {entry.TimesSent} retries: {ex.Message}",
                            stackTrace:       ex.StackTrace,
                            attempts:         entry.TimesSent,
                            CancellationToken.None);
                        _metrics.EventRetryDeadLettered.Add(1);
                    }
                    catch (Exception dlEx)
                    {
                        _logger.LogCritical(dlEx,
                            "IntegrationEventRetryWorker: FAILED to dead-letter exhausted event {EventId} — event may be lost",
                            entry.EventId);
                    }

                    // Mark with terminal state so GetRetryableEventsAsync won't pick it up again.
                    entry.State = EventStateEnum.NotPublished;
                    await eventLog.SaveChangesAsync(CancellationToken.None);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "IntegrationEventRetryWorker: retry failed for event {EventId} ({Type}), attempt {Attempt}/{Max}",
                        entry.EventId, entry.EventTypeShortName, entry.TimesSent, MaxRetries);
                }
            }
        }

        if (retried > 0) _metrics.EventRetrySuccesses.Add(retried);
        if (exhausted > 0) _metrics.EventRetryExhausted.Add(exhausted);
    }

    /// <summary>
    /// Compensating mechanism for the event bus data-loss window. Events marked
    /// <c>Published</c> in the log but older than <see cref="StalePublishedThreshold"/>
    /// may have been lost in transit (e.g., broker connection dropped after the DB
    /// transaction committed but before the broker ACKed the message). This method
    /// re-publishes such events. All downstream handlers are idempotent, so duplicate
    /// delivery is safe.
    /// </summary>
    private async Task RePublishStaleEventsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogReader>();

        var staleEvents = await eventLog.GetStalePublishedEventsAsync(
            StalePublishedThreshold, BatchSize, ct);

        if (staleEvents.Count == 0) return;

        _logger.LogInformation(
            "IntegrationEventRetryWorker: found {Count} stale Published events older than {Threshold}s — re-publishing for safety",
            staleEvents.Count, StalePublishedThreshold.TotalSeconds);

        int rePublished = 0;

        foreach (var entry in staleEvents)
        {
            if (ct.IsCancellationRequested) break;

            if (!_eventTypes.Value.TryGetValue(entry.EventTypeName, out var eventType))
            {
                _logger.LogWarning(
                    "IntegrationEventRetryWorker: unknown event type {Type} for stale event {EventId} — skipping",
                    entry.EventTypeName, entry.EventId);
                continue;
            }

            try
            {
                entry.DeserializeJsonContent(eventType);
                if (entry.IntegrationEvent is null)
                {
                    _logger.LogWarning(
                        "IntegrationEventRetryWorker: failed to deserialize stale event {EventId} ({Type}) — skipping",
                        entry.EventId, entry.EventTypeShortName);
                    continue;
                }

                _eventBus.Publish(entry.IntegrationEvent);

                // Mark the event with an incremented TimesSent so that it ages out of the
                // stale window on the next cycle (CreationTime stays unchanged but the
                // re-publish is recorded). If the handler processes it, the
                // ProcessedIdempotencyKey table prevents double-execution.
                entry.TimesSent++;
                await eventLog.SaveChangesAsync(ct);

                rePublished++;
                _logger.LogInformation(
                    "IntegrationEventRetryWorker: re-published stale event {EventId} ({Type}) — " +
                    "handlers will deduplicate via idempotency keys",
                    entry.EventId, entry.EventTypeShortName);
            }
            catch (Exception ex)
            {
                // Re-publish failure on a stale event is non-critical — the event was
                // already marked Published, so it will be retried on the next cycle.
                _logger.LogWarning(ex,
                    "IntegrationEventRetryWorker: failed to re-publish stale event {EventId} ({Type})",
                    entry.EventId, entry.EventTypeShortName);

                entry.State = EventStateEnum.PublishedFailed;
                await eventLog.SaveChangesAsync(CancellationToken.None);
            }
        }

        if (rePublished > 0)
        {
            _logger.LogInformation(
                "IntegrationEventRetryWorker: re-published {Count} stale events", rePublished);
            _metrics.EventRetrySuccesses.Add(rePublished);
        }
    }
}
