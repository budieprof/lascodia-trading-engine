using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationCompletionPublisher
{
    private static readonly TimeSpan ReplayBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OptimizationCompletionPublisher> _logger;
    private readonly TradingMetrics _metrics;

    public OptimizationCompletionPublisher(
        IServiceScopeFactory scopeFactory,
        ILogger<OptimizationCompletionPublisher> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task PrepareAsync(
        OptimizationRun run,
        IWriteApplicationDbContext writeCtx,
        OptimizationCompletedIntegrationEvent completedEvent,
        CancellationToken ct)
    {
        OptimizationRunProgressTracker.SetStage(
            run,
            OptimizationExecutionStage.CompletionPublication,
            OptimizationRunProgressTracker.GetDefaultStageMessage(OptimizationExecutionStage.CompletionPublication),
            DateTime.UtcNow);
        run.CompletionPublicationPayloadJson = JsonSerializer.Serialize(completedEvent);
        run.CompletionPublicationStatus ??= OptimizationCompletionPublicationStatus.Pending;
        run.CompletionPublicationErrorMessage = null;
        await writeCtx.SaveChangesAsync(ct);
    }

    public async Task PublishWithFallbackAsync(
        long runId,
        OptimizationCompletedIntegrationEvent completedEvent,
        CancellationToken waitCt)
    {
        var waitSw = Stopwatch.StartNew();
        var publishTask = PersistCompletionEventInFreshScopeAsync(runId, completedEvent);
        try
        {
            await publishTask.WaitAsync(waitCt);
            _metrics.OptimizationCompletionReplayWaitMs.Record(waitSw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (waitCt.IsCancellationRequested)
        {
            _metrics.OptimizationCompletionReplayWaitMs.Record(waitSw.Elapsed.TotalMilliseconds);
            _logger.LogWarning(
                "Optimization completion publisher: timed out waiting for event {EventId} of run {RunId}; detached publish will continue",
                completedEvent.Id,
                runId);
            _ = publishTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(
                            t.Exception,
                            "Optimization completion publisher: detached publish failed for event {EventId} of run {RunId}",
                            completedEvent.Id,
                            runId);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    public async Task<int> ReplayPendingAsync(int batchSize, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeCtx.GetDbContext();
        var replayCutoffUtc = DateTime.UtcNow - ReplayBackoff;

        var runsToReplay = await writeDb.Set<OptimizationRun>()
            .Where(r => !r.IsDeleted
                     && (r.Status == OptimizationRunStatus.Completed
                      || r.Status == OptimizationRunStatus.Approved
                      || r.Status == OptimizationRunStatus.Rejected)
                     && r.CompletionPublicationPayloadJson != null
                     && (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Pending
                      || r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Failed)
                     && (r.CompletionPublicationLastAttemptAt == null
                      || r.CompletionPublicationLastAttemptAt <= replayCutoffUtc))
            .OrderBy(r => r.CompletionPublicationLastAttemptAt ?? r.CompletedAt ?? r.ApprovedAt ?? (DateTime?)r.QueuedAt)
            .Take(batchSize)
            .Select(r => new { r.Id, r.CompletionPublicationPayloadJson })
            .ToListAsync(ct);

        foreach (var runInfo in runsToReplay)
        {
            if (string.IsNullOrWhiteSpace(runInfo.CompletionPublicationPayloadJson))
                continue;

            OptimizationCompletedIntegrationEvent? completedEvent;
            try
            {
                completedEvent = JsonSerializer.Deserialize<OptimizationCompletedIntegrationEvent>(runInfo.CompletionPublicationPayloadJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Optimization completion publisher: completion payload for run {RunId} is malformed; replay skipped",
                    runInfo.Id);
                continue;
            }

            if (completedEvent is null)
                continue;

            _metrics.OptimizationCompletionReplayAttempts.Add(1);
            using var replayWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            replayWaitCts.CancelAfter(WaitTimeout);
            await PublishWithFallbackAsync(runInfo.Id, completedEvent, replayWaitCts.Token);
        }

        return runsToReplay.Count;
    }

    private async Task PersistCompletionEventInFreshScopeAsync(
        long runId,
        OptimizationCompletedIntegrationEvent completedEvent)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        IWriteApplicationDbContext? writeCtx = null;
        OptimizationRun? run = null;

        try
        {
            writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var eventService = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
            var writeDb = writeCtx.GetDbContext();
            run = await writeDb.Set<OptimizationRun>()
                .FirstOrDefaultAsync(r => r.Id == runId && !r.IsDeleted, CancellationToken.None);

            if (run is not null)
            {
                run.CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Pending;
                run.CompletionPublicationLastAttemptAt = DateTime.UtcNow;
                run.CompletionPublicationAttempts++;
                run.CompletionPublicationErrorMessage = null;
                await writeCtx.SaveChangesAsync(CancellationToken.None);
            }

            await eventService.SaveAndPublish(writeCtx, completedEvent);

            if (run is not null)
            {
                run.CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Published;
                run.CompletionPublicationCompletedAt = DateTime.UtcNow;
                run.CompletionPublicationErrorMessage = null;
                OptimizationRunProgressTracker.SetTerminalStageFromStatus(run, run.CompletionPublicationCompletedAt.Value);
                var terminalAt = run.ApprovedAt ?? run.CompletedAt ?? run.CompletionPublicationLastAttemptAt;
                if (terminalAt.HasValue)
                {
                    _metrics.OptimizationCompletionPublicationLagMs.Record(
                        Math.Max(0, (run.CompletionPublicationCompletedAt.Value - terminalAt.Value).TotalMilliseconds));
                }
                await writeCtx.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _metrics.OptimizationCompletionReplayFailures.Add(1);

            if (run is not null && writeCtx is not null)
            {
                run.CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Failed;
                run.CompletionPublicationErrorMessage = Truncate(ex.Message, 500);
                OptimizationRunProgressTracker.RecordOperationalIssue(
                    run,
                    "CompletionReplayFailed",
                    $"Completion replay failed: {ex.Message}",
                    DateTime.UtcNow);
                await writeCtx.SaveChangesAsync(CancellationToken.None);
            }

            _logger.LogError(ex,
                "Optimization completion publisher: failed to persist/publish completion event {EventId} for run {RunId}",
                completedEvent.Id,
                runId);

            var deadLetterSink = scope.ServiceProvider.GetService<IDeadLetterSink>();
            if (deadLetterSink is not null)
            {
                await deadLetterSink.WriteAsync(
                    handlerName: nameof(OptimizationCompletionPublisher),
                    eventType: nameof(OptimizationCompletedIntegrationEvent),
                    eventPayloadJson: JsonSerializer.Serialize(completedEvent),
                    errorMessage: ex.Message,
                    stackTrace: ex.ToString(),
                    attempts: Math.Max(1, run?.CompletionPublicationAttempts ?? 1),
                    ct: CancellationToken.None);
            }
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength];
    }
}
