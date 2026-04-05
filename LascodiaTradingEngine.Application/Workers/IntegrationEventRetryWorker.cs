using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;

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
    private const int MaxRetries = 5;
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
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
        ILogger<IntegrationEventRetryWorker> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _eventBus     = eventBus;
        _logger       = logger;
        _metrics      = metrics;

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

                _logger.LogWarning(ex,
                    "IntegrationEventRetryWorker: retry failed for event {EventId} ({Type}), attempt {Attempt}/{Max}",
                    entry.EventId, entry.EventTypeShortName, entry.TimesSent, MaxRetries);
            }
        }

        if (retried > 0) _metrics.EventRetrySuccesses.Add(retried);
        if (exhausted > 0) _metrics.EventRetryExhausted.Add(exhausted);
    }
}
