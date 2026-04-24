using System.Diagnostics;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Synchronises <see cref="EconomicEvent"/> rows with the configured economic calendar feed,
/// including upcoming-event ingestion and post-release actual patching.
/// </summary>
public sealed class EconomicCalendarWorker : BackgroundService
{
    internal const string WorkerName = nameof(EconomicCalendarWorker);

    private const int TitleMaxLength = 200;
    private const int CurrencyMaxLength = 3;
    private const int ForecastMaxLength = 50;
    private const int PreviousMaxLength = 50;
    private const int ActualMaxLength = 50;
    private const int ExternalKeyMaxLength = 200;
    private const int ReasonMaxLength = 1000;
    private const double MinPollingIntervalHours = 0.25;
    private const double MaxPollingIntervalHours = 24;
    private const int MinLookaheadDays = 1;
    private const int MaxLookaheadDays = 30;
    private const int MinActualsPatchBatchSize = 1;
    private const int MaxActualsPatchBatchSize = 500;
    private const int MinStaleEventCutoffDays = 1;
    private const int MaxStaleEventCutoffDays = 30;
    private const int MinFeedCallTimeoutSeconds = 1;
    private const int MaxFeedCallTimeoutSeconds = 300;
    private const int MinRetryCount = 0;
    private const int MaxRetryCount = 5;
    private const int MinActualsPatchMaxConcurrency = 1;
    private const int MaxActualsPatchMaxConcurrency = 20;
    private const int MinThreshold = 1;
    private const int MaxThreshold = 20;
    private const int MaxLoopBackoffHours = 24;
    private const string DistributedLockKey = "workers:economic-calendar:cycle";
    private const string FeedCircuitBreakerAlertDedupKey = "EconomicCalendar:FeedCircuitBreaker";
    private const string SustainedEmptyFeedAlertDedupKey = "EconomicCalendar:SustainedEmptyFetches";
    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromHours(6);

    private readonly ILogger<EconomicCalendarWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EconomicCalendarOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveLoopFailures;
    private long _consecutiveEmptyFetches;
    private long _consecutiveFeedFailures;
    private bool _missingDistributedLockWarningEmitted;

    public EconomicCalendarWorker(
        ILogger<EconomicCalendarWorker> logger,
        IServiceScopeFactory scopeFactory,
        EconomicCalendarOptions options,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;

        _metrics?.RegisterEconEmptyFetchGauge(() => Interlocked.Read(ref _consecutiveEmptyFetches));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = BuildSettings();

        _logger.LogInformation(
            "{Worker} starting (interval={Interval}h, lookahead={Lookahead}d, staleCutoff={Cutoff}d, patchBatch={Batch}, feedTimeout={Timeout}s, feedRetries={Retries}, patchConcurrency={Concurrency}, patchRetries={PatchRetries}, skipWeekends={SkipWeekends}, circuitBreaker={CircuitBreaker})",
            WorkerName,
            initialSettings.PollingInterval.TotalHours,
            initialSettings.LookaheadDays,
            initialSettings.StaleEventCutoffDays,
            initialSettings.ActualsPatchBatchSize,
            initialSettings.FeedCallTimeoutSeconds,
            initialSettings.FeedRetryCount,
            initialSettings.ActualsPatchMaxConcurrency,
            initialSettings.ActualsPatchRetryCount,
            initialSettings.SkipWeekends,
            initialSettings.FeedCircuitBreakerThreshold);

        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Synchronises upcoming macroeconomic events, patches released actuals, and surfaces feed-health issues that could stale the news-halt pipeline.",
            initialSettings.PollingInterval);

        var currentPollInterval = initialSettings.PollingInterval;

        try
        {
            try
            {
                var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                long cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    currentPollInterval = result.NextDelay;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    if (result.PendingActualCount.HasValue)
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.PendingActualCount.Value);

                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.EconCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else
                    {
                        if (result.CreatedCount > 0 || result.PatchedActualCount > 0)
                        {
                            _logger.LogInformation(
                                "{Worker}: cycle complete — created={Created}, patchedActuals={Patched}, pendingActuals={Pending}, fetchedUpcoming={FetchedUpcoming}, refreshedActualCandidates={ActualCandidates}.",
                                WorkerName,
                                result.CreatedCount,
                                result.PatchedActualCount,
                                result.PendingActualCount ?? 0,
                                result.FetchedUpcomingCount,
                                result.InlineActualCandidateCount);
                        }

                        if (result.DispatchedAlertCount > 0 || result.ResolvedAlertCount > 0)
                        {
                            _logger.LogWarning(
                                "{Worker}: feed health state changed — alerts dispatched={Dispatched}, resolved={Resolved}, consecutiveEmpty={Empty}, consecutiveFailures={Failures}.",
                                WorkerName,
                                result.DispatchedAlertCount,
                                result.ResolvedAlertCount,
                                result.ConsecutiveEmptyFetches,
                                result.ConsecutiveFeedFailures);
                        }
                    }

                    if (_consecutiveLoopFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveLoopFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName,
                            _consecutiveLoopFailures);
                    }

                    _consecutiveLoopFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveLoopFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "economic_calendar_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(
                        CalculateDelay(_consecutiveLoopFailures, currentPollInterval),
                        _timeProvider,
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped", WorkerName);
        }
    }

    internal static TimeSpan CalculateDelay(int consecutiveFailures, TimeSpan baseDelay)
    {
        if (baseDelay <= TimeSpan.Zero)
            baseDelay = DefaultPollInterval;

        if (consecutiveFailures <= 0)
            return baseDelay;

        double delayHours = Math.Min(
            baseDelay.TotalHours * Math.Pow(2, consecutiveFailures - 1),
            MaxLoopBackoffHours);

        return TimeSpan.FromHours(delayHours);
    }

    internal async Task<EconomicCalendarWorkerCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var settings = BuildSettings();
        var now = _timeProvider.GetUtcNow();

        if (settings.SkipWeekends && IsWeekend(now))
        {
            return EconomicCalendarWorkerCycleResult.Skipped(
                settings,
                settings.PollingInterval,
                "weekend");
        }

        if (_distributedLock is null)
        {
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate economic-calendar sync cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }

            return await RunCycleCoreAsync(settings, now.UtcDateTime, ct);
        }

        var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
        if (cycleLock is null)
            return EconomicCalendarWorkerCycleResult.Skipped(settings, settings.PollingInterval, "lock_busy");

        await using (cycleLock)
        {
            return await RunCycleCoreAsync(settings, now.UtcDateTime, ct);
        }
    }

    private async Task<EconomicCalendarWorkerCycleResult> RunCycleCoreAsync(
        EconomicCalendarWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var feed = serviceProvider.GetRequiredService<IEconomicCalendarFeed>();
        var mediator = serviceProvider.GetService<IMediator>();

        var currencies = await GetActiveCurrenciesAsync(db, ct);
        if (currencies.Count == 0)
        {
            int resolvedAlerts = await ResolveFeedAlertsAsync(serviceProvider, writeContext, db, nowUtc, ct);
            if (resolvedAlerts > 0)
            {
                _metrics?.EconAlertTransitions.Add(
                    resolvedAlerts,
                    new KeyValuePair<string, object?>("transition", "resolved"));
            }

            return new EconomicCalendarWorkerCycleResult(
                settings,
                NextDelay: settings.PollingInterval,
                CreatedCount: 0,
                PatchedActualCount: 0,
                PendingActualCount: 0,
                FetchedUpcomingCount: 0,
                InlineActualCandidateCount: 0,
                DispatchedAlertCount: 0,
                ResolvedAlertCount: resolvedAlerts,
                ConsecutiveEmptyFetches: Interlocked.Read(ref _consecutiveEmptyFetches),
                ConsecutiveFeedFailures: Interlocked.Read(ref _consecutiveFeedFailures),
                SkippedReason: "no_active_currencies");
        }

        var ingestionResult = await IngestUpcomingEventsAsync(
            serviceProvider,
            writeContext,
            db,
            feed,
            mediator,
            settings,
            currencies,
            nowUtc,
            ct);

        var patchResult = await PatchReleasedActualsAsync(
            serviceProvider,
            writeContext,
            db,
            feed,
            mediator,
            settings,
            nowUtc,
            ct);

        int alertDispatches = 0;
        int alertResolutions = 0;
        var alertResult = await SynchronizeFeedAlertsAsync(
            serviceProvider,
            writeContext,
            db,
            settings,
            currencies.Count,
            ingestionResult,
            nowUtc,
            ct);
        alertDispatches += alertResult.DispatchedCount;
        alertResolutions += alertResult.ResolvedCount;

        if (alertDispatches > 0)
        {
            _metrics?.EconAlertTransitions.Add(
                alertDispatches,
                new KeyValuePair<string, object?>("transition", "dispatched"));
        }

        if (alertResolutions > 0)
        {
            _metrics?.EconAlertTransitions.Add(
                alertResolutions,
                new KeyValuePair<string, object?>("transition", "resolved"));
        }

        _metrics?.EconPendingActualsBacklog.Record(patchResult.PendingCount);

        var nextDelay = patchResult.HasPendingHighImpactActuals
            ? settings.AcceleratedPollingInterval
            : settings.PollingInterval;

        return new EconomicCalendarWorkerCycleResult(
            settings,
            NextDelay: nextDelay,
            CreatedCount: ingestionResult.CreatedCount,
            PatchedActualCount: patchResult.PatchedCount,
            PendingActualCount: patchResult.PendingCount,
            FetchedUpcomingCount: ingestionResult.FetchedCount,
            InlineActualCandidateCount: patchResult.InlineActualCandidateCount,
            DispatchedAlertCount: alertDispatches,
            ResolvedAlertCount: alertResolutions,
            ConsecutiveEmptyFetches: Interlocked.Read(ref _consecutiveEmptyFetches),
            ConsecutiveFeedFailures: Interlocked.Read(ref _consecutiveFeedFailures),
            SkippedReason: null);
    }

    private async Task<EconomicCalendarIngestionResult> IngestUpcomingEventsAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IEconomicCalendarFeed calendarFeed,
        IMediator? mediator,
        EconomicCalendarWorkerSettings settings,
        IReadOnlyCollection<string> currencies,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var circuitState = EvaluateCircuitBreaker(settings);
        if (circuitState.SkipFetch)
        {
            _metrics?.EconCircuitBreakerSkips.Add(1);
            _logger.LogWarning(
                "{Worker}: ingestion skipped because the feed circuit breaker is open ({Failures} consecutive failures, threshold={Threshold}).",
                WorkerName,
                circuitState.ConsecutiveFailures,
                settings.FeedCircuitBreakerThreshold);

            return EconomicCalendarIngestionResult.ForCircuitBreakerSkip(circuitState.ConsecutiveFailures);
        }

        var fromUtc = nowUtc;
        var toUtc = nowUtc.AddDays(settings.LookaheadDays);

        var incoming = await TryFetchUpcomingEventsAsync(
            calendarFeed,
            currencies,
            fromUtc,
            toUtc,
            settings,
            ct);

        if (incoming is null)
        {
            return EconomicCalendarIngestionResult.Failed(
                Interlocked.Read(ref _consecutiveFeedFailures),
                Interlocked.Read(ref _consecutiveEmptyFetches));
        }

        if (incoming.Count == 0)
        {
            long emptyCount = Interlocked.Increment(ref _consecutiveEmptyFetches);

            if (emptyCount >= settings.SustainedEmptyFetchThreshold)
            {
                _logger.LogCritical(
                    "{Worker}: feed returned zero events for {Count} consecutive cycle(s); macro-event coverage may be stale.",
                    WorkerName,
                    emptyCount);
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "ingestion"),
                    new KeyValuePair<string, object?>("reason", "sustained_empty"));
            }
            else
            {
                _logger.LogDebug("{Worker}: no upcoming economic events returned by feed.", WorkerName);
            }

            return EconomicCalendarIngestionResult.Empty(
                Interlocked.Read(ref _consecutiveFeedFailures),
                emptyCount);
        }

        Interlocked.Exchange(ref _consecutiveEmptyFetches, 0);

        var existingEvents = await db.Set<EconomicEvent>()
            .AsNoTracking()
            .Where(eventRow => !eventRow.IsDeleted
                            && eventRow.ScheduledAt >= fromUtc
                            && eventRow.ScheduledAt <= toUtc)
            .Select(eventRow => new ExistingEconomicEventProjection(
                eventRow.Title,
                eventRow.Currency,
                eventRow.ScheduledAt,
                eventRow.ExternalKey))
            .ToListAsync(ct);

        var externalKeySet = existingEvents
            .Where(eventRow => !string.IsNullOrWhiteSpace(eventRow.ExternalKey))
            .Select(eventRow => eventRow.ExternalKey!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var compositeKeySet = existingEvents
            .Select(eventRow => DedupeKey(eventRow.Title, eventRow.Currency, eventRow.ScheduledAt))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int createdCount = 0;
        int missingExternalKeyCount = 0;

        foreach (var candidate in incoming)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryNormalizeIncomingEvent(candidate, out var normalized))
            {
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "ingestion"),
                    new KeyValuePair<string, object?>("reason", "invalid_event"));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(normalized.ExternalKey) && externalKeySet.Contains(normalized.ExternalKey))
                continue;

            string dedupeKey = DedupeKey(normalized.Title, normalized.Currency, normalized.ScheduledAt);
            if (compositeKeySet.Contains(dedupeKey))
                continue;

            if (string.IsNullOrWhiteSpace(normalized.ExternalKey))
            {
                missingExternalKeyCount++;
                _logger.LogWarning(
                    "{Worker}: ingested event '{Title}' ({Currency} at {ScheduledAt:u}) without ExternalKey; fallback actual patching will rely on composite matching only.",
                    WorkerName,
                    normalized.Title,
                    normalized.Currency,
                    normalized.ScheduledAt);
            }

            var entity = new EconomicEvent
            {
                Title = normalized.Title,
                Currency = normalized.Currency,
                Impact = normalized.Impact,
                ScheduledAt = normalized.ScheduledAt,
                Forecast = normalized.Forecast,
                Previous = normalized.Previous,
                Actual = normalized.Actual,
                ExternalKey = normalized.ExternalKey,
                Source = normalized.Source
            };

            db.Set<EconomicEvent>().Add(entity);

            try
            {
                await writeContext.SaveChangesAsync(ct);
                createdCount++;
                if (!string.IsNullOrWhiteSpace(normalized.ExternalKey))
                    externalKeySet.Add(normalized.ExternalKey);
                compositeKeySet.Add(dedupeKey);
            }
            catch (Exception ex)
            {
                DetachIfAdded(db, entity);
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "ingestion_persist"),
                    new KeyValuePair<string, object?>("reason", "db_error"));
                _logger.LogError(
                    ex,
                    "{Worker}: failed to persist economic event '{Title}' ({Currency} at {ScheduledAt:u}).",
                    WorkerName,
                    normalized.Title,
                    normalized.Currency,
                    normalized.ScheduledAt);
            }
        }

        _metrics?.EconEventsIngested.Add(createdCount);

        if (createdCount > 0 && mediator is not null)
        {
            await TryLogDecisionAsync(
                mediator,
                entityType: "EconomicCalendar",
                entityId: 0,
                decisionType: "Ingestion",
                outcome: "Completed",
                reason: $"Created {createdCount} of {incoming.Count} fetched economic events for {currencies.Count} currencies ({fromUtc:u} to {toUtc:u}); missingExternalKey={missingExternalKeyCount}.",
                ct);
        }

        return new EconomicCalendarIngestionResult(
            FetchedCount: incoming.Count,
            CreatedCount: createdCount,
            MissingExternalKeyCount: missingExternalKeyCount,
            CircuitBreakerSkipped: false,
            ConsecutiveFeedFailures: Interlocked.Read(ref _consecutiveFeedFailures),
            ConsecutiveEmptyFetches: 0,
            FetchSucceeded: true,
            FetchReturnedData: true);
    }

    private async Task<EconomicCalendarActualPatchResult> PatchReleasedActualsAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IEconomicCalendarFeed calendarFeed,
        IMediator? mediator,
        EconomicCalendarWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var staleCutoffUtc = nowUtc.AddDays(-settings.StaleEventCutoffDays);

        var pendingEvents = await db.Set<EconomicEvent>()
            .Where(eventRow => !eventRow.IsDeleted
                            && eventRow.Actual == null
                            && eventRow.ScheduledAt < nowUtc
                            && eventRow.ScheduledAt >= staleCutoffUtc)
            .OrderByDescending(eventRow =>
                GetImpactPriority(eventRow.Impact))
            .ThenBy(eventRow => eventRow.ScheduledAt)
            .Take(settings.ActualsPatchBatchSize)
            .ToListAsync(ct);

        if (pendingEvents.Count == 0)
            return EconomicCalendarActualPatchResult.Empty;

        int patchedCount = 0;
        int patchedViaFallback = 0;
        int inlineActualCandidateCount = 0;

        var refreshFromUtc = pendingEvents.Min(eventRow => eventRow.ScheduledAt).AddDays(-1);
        var refreshToUtc = nowUtc.AddDays(1);
        var pendingCurrencies = pendingEvents
            .Select(eventRow => NormalizeCurrency(eventRow.Currency))
            .Where(currency => !string.IsNullOrWhiteSpace(currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var refreshedEvents = await TryFetchRecentEventsForActualsAsync(
            calendarFeed,
            pendingCurrencies,
            refreshFromUtc,
            refreshToUtc,
            settings,
            ct);

        if (refreshedEvents is not null && refreshedEvents.Count > 0)
        {
            var actualByExternalKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var actualByCompositeKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var refreshed in refreshedEvents)
            {
                if (!TryNormalizeIncomingEvent(refreshed, out var normalized))
                    continue;

                if (string.IsNullOrWhiteSpace(normalized.Actual))
                    continue;

                inlineActualCandidateCount++;

                if (!string.IsNullOrWhiteSpace(normalized.ExternalKey) && !actualByExternalKey.ContainsKey(normalized.ExternalKey))
                    actualByExternalKey[normalized.ExternalKey] = normalized.Actual!;

                string compositeKey = DedupeKey(normalized.Title, normalized.Currency, normalized.ScheduledAt);
                if (!actualByCompositeKey.ContainsKey(compositeKey))
                    actualByCompositeKey[compositeKey] = normalized.Actual!;
            }

            foreach (var pending in pendingEvents.Where(eventRow => eventRow.Actual == null))
            {
                string? patchedActual = null;

                if (!string.IsNullOrWhiteSpace(pending.ExternalKey) &&
                    actualByExternalKey.TryGetValue(pending.ExternalKey, out var actualFromKey))
                {
                    patchedActual = actualFromKey;
                }
                else
                {
                    actualByCompositeKey.TryGetValue(
                        DedupeKey(pending.Title, pending.Currency, pending.ScheduledAt),
                        out patchedActual);
                }

                if (string.IsNullOrWhiteSpace(patchedActual))
                    continue;

                pending.Actual = Truncate(patchedActual.Trim(), ActualMaxLength);
                patchedCount++;
            }

            if (patchedCount > 0)
                await writeContext.SaveChangesAsync(ct);
        }

        var unresolvedFallbackCandidates = pendingEvents
            .Where(eventRow => eventRow.Actual == null && !string.IsNullOrWhiteSpace(eventRow.ExternalKey))
            .ToList();

        if (unresolvedFallbackCandidates.Count > 0)
        {
            var actualByEventId = new Dictionary<long, string>();
            using var fallbackGate = new SemaphoreSlim(settings.ActualsPatchMaxConcurrency);
            var fallbackTasks = unresolvedFallbackCandidates.Select(async pending =>
            {
                await fallbackGate.WaitAsync(ct);
                try
                {
                    var actual = await TryFetchActualAsync(
                        calendarFeed,
                        pending.Id,
                        pending.ExternalKey!,
                        settings,
                        ct);

                    if (string.IsNullOrWhiteSpace(actual))
                        return;

                    lock (actualByEventId)
                    {
                        actualByEventId[pending.Id] = Truncate(actual.Trim(), ActualMaxLength);
                    }
                }
                finally
                {
                    fallbackGate.Release();
                }
            });

            await Task.WhenAll(fallbackTasks);

            foreach (var pending in unresolvedFallbackCandidates)
            {
                if (!actualByEventId.TryGetValue(pending.Id, out var actual))
                    continue;

                pending.Actual = actual;
                patchedCount++;
                patchedViaFallback++;
            }
        }

        if (patchedViaFallback > 0)
            await writeContext.SaveChangesAsync(ct);

        _metrics?.EconActualsPatched.Add(patchedCount);

        if (patchedCount > 0 && mediator is not null)
        {
            await TryLogDecisionAsync(
                mediator,
                entityType: "EconomicCalendar",
                entityId: 0,
                decisionType: "ActualsPatch",
                outcome: "Completed",
                reason: $"Patched actuals for {patchedCount} of {pendingEvents.Count} pending economic events (staleCutoff={staleCutoffUtc:u}, inlineCandidates={inlineActualCandidateCount}).",
                ct);
        }

        bool hasPendingHighImpactActuals = pendingEvents.Any(eventRow =>
            eventRow.Actual == null &&
            eventRow.Impact == EconomicImpact.High &&
            eventRow.ScheduledAt >= nowUtc.AddHours(-2));

        return new EconomicCalendarActualPatchResult(
            PendingCount: pendingEvents.Count,
            PatchedCount: patchedCount,
            InlineActualCandidateCount: inlineActualCandidateCount,
            HasPendingHighImpactActuals: hasPendingHighImpactActuals);
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>?> TryFetchUpcomingEventsAsync(
        IEconomicCalendarFeed calendarFeed,
        IReadOnlyCollection<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        EconomicCalendarWorkerSettings settings,
        CancellationToken ct)
    {
        int maxAttempts = 1 + settings.FeedRetryCount;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.FeedCallTimeoutSeconds));

                var result = await calendarFeed.GetUpcomingEventsAsync(currencies, fromUtc, toUtc, timeoutCts.Token);
                Interlocked.Exchange(ref _consecutiveFeedFailures, 0);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "ingestion"),
                    new KeyValuePair<string, object?>("reason", "timeout"));
                _logger.LogWarning(
                    "{Worker}: feed timeout on ingestion attempt {Attempt}/{Max} ({Timeout}s).",
                    WorkerName,
                    attempt,
                    maxAttempts,
                    settings.FeedCallTimeoutSeconds);

                if (attempt >= maxAttempts)
                {
                    Interlocked.Increment(ref _consecutiveFeedFailures);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "ingestion"),
                    new KeyValuePair<string, object?>("reason", attempt < maxAttempts ? "transient" : "exhausted"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: feed error on ingestion attempt {Attempt}/{Max}.",
                    WorkerName,
                    attempt,
                    maxAttempts);

                if (attempt >= maxAttempts)
                {
                    Interlocked.Increment(ref _consecutiveFeedFailures);
                    return null;
                }
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)),
                _timeProvider,
                ct);
        }

        return null;
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>?> TryFetchRecentEventsForActualsAsync(
        IEconomicCalendarFeed calendarFeed,
        IReadOnlyCollection<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        EconomicCalendarWorkerSettings settings,
        CancellationToken ct)
    {
        if (currencies.Count == 0)
            return [];

        int maxAttempts = 1 + settings.ActualsPatchRetryCount;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.FeedCallTimeoutSeconds));

                return await calendarFeed.GetUpcomingEventsAsync(currencies, fromUtc, toUtc, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "actuals_refresh"),
                    new KeyValuePair<string, object?>("reason", "timeout"));
                _logger.LogWarning(
                    "{Worker}: timeout refreshing released-event window for actuals on attempt {Attempt}/{Max} ({Timeout}s).",
                    WorkerName,
                    attempt,
                    maxAttempts,
                    settings.FeedCallTimeoutSeconds);

                if (attempt >= maxAttempts)
                    return null;
            }
            catch (Exception ex)
            {
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "actuals_refresh"),
                    new KeyValuePair<string, object?>("reason", attempt < maxAttempts ? "transient" : "exhausted"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: error refreshing released-event window for actuals on attempt {Attempt}/{Max}.",
                    WorkerName,
                    attempt,
                    maxAttempts);

                if (attempt >= maxAttempts)
                    return null;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)),
                _timeProvider,
                ct);
        }

        return null;
    }

    private async Task<string?> TryFetchActualAsync(
        IEconomicCalendarFeed calendarFeed,
        long eventId,
        string externalKey,
        EconomicCalendarWorkerSettings settings,
        CancellationToken ct)
    {
        int maxAttempts = 1 + settings.ActualsPatchRetryCount;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.FeedCallTimeoutSeconds));

                var actual = await calendarFeed.GetActualAsync(externalKey, timeoutCts.Token);
                return string.IsNullOrWhiteSpace(actual) ? null : actual;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "actuals_patch"),
                    new KeyValuePair<string, object?>("reason", "timeout"));
                _logger.LogWarning(
                    "{Worker}: timeout fetching actual for event {EventId} on attempt {Attempt}/{Max} ({Timeout}s).",
                    WorkerName,
                    eventId,
                    attempt,
                    maxAttempts,
                    settings.FeedCallTimeoutSeconds);

                if (attempt >= maxAttempts)
                    return null;
            }
            catch (Exception ex)
            {
                _metrics?.EconFeedErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("phase", "actuals_patch"),
                    new KeyValuePair<string, object?>("reason", "error"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: error fetching actual for event {EventId} on attempt {Attempt}/{Max}.",
                    WorkerName,
                    eventId,
                    attempt,
                    maxAttempts);

                if (attempt >= maxAttempts)
                    return null;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)),
                _timeProvider,
                ct);
        }

        return null;
    }

    private async Task<FeedAlertSyncResult> SynchronizeFeedAlertsAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        EconomicCalendarWorkerSettings settings,
        int currencyCount,
        EconomicCalendarIngestionResult ingestionResult,
        DateTime nowUtc,
        CancellationToken ct)
    {
        int dispatchedCount = 0;
        int resolvedCount = 0;

        if (ingestionResult.CircuitBreakerSkipped ||
            ingestionResult.ConsecutiveFeedFailures >= settings.FeedCircuitBreakerThreshold)
        {
            bool dispatched = await UpsertAndDispatchAlertAsync(
                serviceProvider,
                writeContext,
                db,
                FeedCircuitBreakerAlertDedupKey,
                AlertSeverity.Critical,
                BuildFeedCircuitBreakerConditionJson(
                    ingestionResult.ConsecutiveFeedFailures,
                    settings.FeedCircuitBreakerThreshold,
                    currencyCount,
                    nowUtc),
                $"Economic calendar ingestion circuit breaker is open after {ingestionResult.ConsecutiveFeedFailures} consecutive failure(s). Upcoming-event coverage may be stale until the next successful probe.",
                ct);

            if (dispatched)
                dispatchedCount++;
        }
        else
        {
            resolvedCount += await ResolveAlertAsync(
                serviceProvider,
                writeContext,
                db,
                FeedCircuitBreakerAlertDedupKey,
                nowUtc,
                ct);
        }

        if (ingestionResult.FetchReturnedData &&
            ingestionResult.ConsecutiveEmptyFetches >= settings.SustainedEmptyFetchThreshold)
        {
            bool dispatched = await UpsertAndDispatchAlertAsync(
                serviceProvider,
                writeContext,
                db,
                SustainedEmptyFeedAlertDedupKey,
                AlertSeverity.High,
                BuildSustainedEmptyFeedConditionJson(
                    ingestionResult.ConsecutiveEmptyFetches,
                    settings.SustainedEmptyFetchThreshold,
                    currencyCount,
                    nowUtc),
                $"Economic calendar feed returned zero events for {ingestionResult.ConsecutiveEmptyFetches} consecutive cycle(s). News-halt protection may be stale or the upstream provider format may have changed.",
                ct);

            if (dispatched)
                dispatchedCount++;
        }
        else if (ingestionResult.FetchReturnedData && ingestionResult.FetchSucceeded)
        {
            resolvedCount += await ResolveAlertAsync(
                serviceProvider,
                writeContext,
                db,
                SustainedEmptyFeedAlertDedupKey,
                nowUtc,
                ct);
        }

        return new FeedAlertSyncResult(dispatchedCount, resolvedCount);
    }

    private async Task<int> ResolveFeedAlertsAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        DateTime nowUtc,
        CancellationToken ct)
    {
        int resolved = 0;
        resolved += await ResolveAlertAsync(serviceProvider, writeContext, db, FeedCircuitBreakerAlertDedupKey, nowUtc, ct);
        resolved += await ResolveAlertAsync(serviceProvider, writeContext, db, SustainedEmptyFeedAlertDedupKey, nowUtc, ct);
        return resolved;
    }

    private async Task<bool> UpsertAndDispatchAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        string deduplicationKey,
        AlertSeverity severity,
        string conditionJson,
        string message,
        CancellationToken ct)
    {
        int cooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
            db,
            AlertCooldownDefaults.CK_Infrastructure,
            AlertCooldownDefaults.Default_Infrastructure,
            ct);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };
            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.DataQualityIssue;
        }

        alert.Severity = severity;
        alert.CooldownSeconds = cooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = Truncate(conditionJson, ReasonMaxLength);

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLikelyAlertDeduplicationRace(serviceProvider, ex))
        {
            DetachIfAdded(db, alert);
            alert = await db.Set<Alert>()
                .FirstAsync(candidate => !candidate.IsDeleted
                                      && candidate.IsActive
                                      && candidate.DeduplicationKey == deduplicationKey, ct);

            alert.AlertType = AlertType.DataQualityIssue;
            alert.Severity = severity;
            alert.CooldownSeconds = cooldownSeconds;
            alert.AutoResolvedAt = null;
            alert.ConditionJson = Truncate(conditionJson, ReasonMaxLength);
            await writeContext.SaveChangesAsync(ct);
        }

        if (alert.LastTriggeredAt.HasValue &&
            nowUtc - NormalizeUtc(alert.LastTriggeredAt.Value) < TimeSpan.FromSeconds(cooldownSeconds))
        {
            return false;
        }

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch alert {DeduplicationKey}.",
                WorkerName,
                deduplicationKey);
            return false;
        }
    }

    private async Task<int> ResolveAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        string deduplicationKey,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
            return 0;

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        if (alert.LastTriggeredAt.HasValue)
        {
            try
            {
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "{Worker}: failed to auto-resolve alert {DeduplicationKey}.",
                    WorkerName,
                    deduplicationKey);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
        return 1;
    }

    private EconomicCalendarWorkerSettings BuildSettings()
    {
        double pollingIntervalHours = Clamp(
            _options.PollingIntervalHours,
            MinPollingIntervalHours,
            MaxPollingIntervalHours);

        var pollingInterval = TimeSpan.FromHours(pollingIntervalHours);
        var acceleratedInterval = TimeSpan.FromHours(
            Math.Min(
                pollingInterval.TotalHours,
                Math.Max(MinPollingIntervalHours, pollingInterval.TotalHours / 4.0)));

        return new EconomicCalendarWorkerSettings(
            PollingInterval: pollingInterval,
            AcceleratedPollingInterval: acceleratedInterval,
            LookaheadDays: Clamp(_options.LookaheadDays, MinLookaheadDays, MaxLookaheadDays),
            ActualsPatchBatchSize: Clamp(_options.ActualsPatchBatchSize, MinActualsPatchBatchSize, MaxActualsPatchBatchSize),
            StaleEventCutoffDays: Clamp(_options.StaleEventCutoffDays, MinStaleEventCutoffDays, MaxStaleEventCutoffDays),
            FeedCallTimeoutSeconds: Clamp(_options.FeedCallTimeoutSeconds, MinFeedCallTimeoutSeconds, MaxFeedCallTimeoutSeconds),
            FeedRetryCount: Clamp(_options.FeedRetryCount, MinRetryCount, MaxRetryCount),
            ActualsPatchRetryCount: Clamp(_options.ActualsPatchRetryCount, MinRetryCount, MaxRetryCount),
            ActualsPatchMaxConcurrency: Clamp(_options.ActualsPatchMaxConcurrency, MinActualsPatchMaxConcurrency, MaxActualsPatchMaxConcurrency),
            SkipWeekends: _options.SkipWeekends,
            FeedCircuitBreakerThreshold: Clamp(_options.FeedCircuitBreakerThreshold, MinThreshold, MaxThreshold),
            SustainedEmptyFetchThreshold: Clamp(_options.SustainedEmptyFetchThreshold, MinThreshold, MaxThreshold));
    }

    private CircuitBreakerState EvaluateCircuitBreaker(EconomicCalendarWorkerSettings settings)
    {
        long failures = Interlocked.Read(ref _consecutiveFeedFailures);
        if (failures < settings.FeedCircuitBreakerThreshold)
            return CircuitBreakerState.Closed(failures);

        if (failures % settings.FeedCircuitBreakerThreshold == 0)
        {
            _logger.LogInformation(
                "{Worker}: feed circuit breaker probe triggered at {Failures} consecutive failure(s).",
                WorkerName,
                failures);
            return CircuitBreakerState.Probe(failures);
        }

        failures = Interlocked.Increment(ref _consecutiveFeedFailures);
        return CircuitBreakerState.Open(failures);
    }

    private static async Task<List<string>> GetActiveCurrenciesAsync(DbContext db, CancellationToken ct)
    {
        var pairs = await db.Set<CurrencyPair>()
            .AsNoTracking()
            .Where(pair => pair.IsActive && !pair.IsDeleted)
            .Select(pair => new { pair.BaseCurrency, pair.QuoteCurrency })
            .ToListAsync(ct);

        return pairs
            .SelectMany(pair => new[] { NormalizeCurrency(pair.BaseCurrency), NormalizeCurrency(pair.QuoteCurrency) })
            .Where(currency => currency.Length == CurrencyMaxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(currency => currency, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryNormalizeIncomingEvent(
        EconomicCalendarEvent incoming,
        out NormalizedEconomicCalendarEvent normalized)
    {
        string title = Truncate((incoming.Title ?? string.Empty).Trim(), TitleMaxLength);
        string currency = NormalizeCurrency(incoming.Currency);

        if (string.IsNullOrWhiteSpace(title) || currency.Length != CurrencyMaxLength)
        {
            normalized = default;
            return false;
        }

        normalized = new NormalizedEconomicCalendarEvent(
            Title: title,
            Currency: currency,
            Impact: incoming.Impact,
            ScheduledAt: NormalizeUtc(incoming.ScheduledAt),
            Forecast: TruncateOrNull(incoming.Forecast, ForecastMaxLength),
            Previous: TruncateOrNull(incoming.Previous, PreviousMaxLength),
            Actual: TruncateOrNull(incoming.Actual, ActualMaxLength),
            ExternalKey: TruncateOrNull(incoming.ExternalKey, ExternalKeyMaxLength),
            Source: incoming.Source);

        return true;
    }

    private static async Task TryLogDecisionAsync(
        IMediator mediator,
        string entityType,
        long entityId,
        string decisionType,
        string outcome,
        string reason,
        CancellationToken ct)
    {
        try
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType = entityType,
                EntityId = entityId,
                DecisionType = decisionType,
                Outcome = outcome,
                Reason = Truncate(reason, ReasonMaxLength),
                Source = WorkerName
            }, ct);
        }
        catch
        {
            // Audit trail is best-effort and must never fail the worker cycle.
        }
    }

    private static string BuildFeedCircuitBreakerConditionJson(
        long consecutiveFailures,
        int threshold,
        int currencyCount,
        DateTime observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            source = WorkerName,
            reason = "FeedCircuitBreaker",
            consecutiveFailures,
            threshold,
            currencyCount,
            observedAtUtc = NormalizeUtc(observedAtUtc)
        });

    private static string BuildSustainedEmptyFeedConditionJson(
        long consecutiveEmptyFetches,
        int threshold,
        int currencyCount,
        DateTime observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            source = WorkerName,
            reason = "SustainedEmptyFetches",
            consecutiveEmptyFetches,
            threshold,
            currencyCount,
            observedAtUtc = NormalizeUtc(observedAtUtc)
        });

    private static bool IsWeekend(DateTimeOffset timestamp)
        => timestamp.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static string DedupeKey(string title, string currency, DateTime scheduledAtUtc)
        => $"{title.Trim().ToUpperInvariant()}|{NormalizeCurrency(currency)}|{NormalizeUtc(scheduledAtUtc):yyyyMMddHHmm}";

    private static int GetImpactPriority(EconomicImpact impact)
        => impact switch
        {
            EconomicImpact.High => 3,
            EconomicImpact.Medium => 2,
            EconomicImpact.Low => 1,
            _ => 0
        };

    private static string NormalizeCurrency(string? currency)
        => (currency ?? string.Empty).Trim().ToUpperInvariant();

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Truncate(value.Trim(), maxLength);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private static double Clamp(double value, double min, double max)
        => Math.Min(Math.Max(value, min), max);

    private static bool IsLikelyAlertDeduplicationRace(IServiceProvider serviceProvider, DbUpdateException ex)
    {
        var classifier = serviceProvider.GetService<IDatabaseExceptionClassifier>();
        if (classifier?.IsUniqueConstraintViolation(ex) == true)
            return true;

        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("DeduplicationKey", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    private static void DetachIfAdded(DbContext db, object entity)
    {
        var entry = db.Entry(entity);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }

    internal readonly record struct EconomicCalendarWorkerCycleResult(
        EconomicCalendarWorkerSettings Settings,
        TimeSpan NextDelay,
        int CreatedCount,
        int PatchedActualCount,
        int? PendingActualCount,
        int FetchedUpcomingCount,
        int InlineActualCandidateCount,
        int DispatchedAlertCount,
        int ResolvedAlertCount,
        long ConsecutiveEmptyFetches,
        long ConsecutiveFeedFailures,
        string? SkippedReason)
    {
        public static EconomicCalendarWorkerCycleResult Skipped(
            EconomicCalendarWorkerSettings settings,
            TimeSpan nextDelay,
            string reason)
            => new(
                settings,
                nextDelay,
                CreatedCount: 0,
                PatchedActualCount: 0,
                PendingActualCount: null,
                FetchedUpcomingCount: 0,
                InlineActualCandidateCount: 0,
                DispatchedAlertCount: 0,
                ResolvedAlertCount: 0,
                ConsecutiveEmptyFetches: 0,
                ConsecutiveFeedFailures: 0,
                SkippedReason: reason);
    }

    internal readonly record struct EconomicCalendarWorkerSettings(
        TimeSpan PollingInterval,
        TimeSpan AcceleratedPollingInterval,
        int LookaheadDays,
        int ActualsPatchBatchSize,
        int StaleEventCutoffDays,
        int FeedCallTimeoutSeconds,
        int FeedRetryCount,
        int ActualsPatchRetryCount,
        int ActualsPatchMaxConcurrency,
        bool SkipWeekends,
        int FeedCircuitBreakerThreshold,
        int SustainedEmptyFetchThreshold);

    private readonly record struct EconomicCalendarIngestionResult(
        int FetchedCount,
        int CreatedCount,
        int MissingExternalKeyCount,
        bool CircuitBreakerSkipped,
        long ConsecutiveFeedFailures,
        long ConsecutiveEmptyFetches,
        bool FetchSucceeded,
        bool FetchReturnedData)
    {
        public static EconomicCalendarIngestionResult ForCircuitBreakerSkip(long failures)
            => new(
                FetchedCount: 0,
                CreatedCount: 0,
                MissingExternalKeyCount: 0,
                CircuitBreakerSkipped: true,
                ConsecutiveFeedFailures: failures,
                ConsecutiveEmptyFetches: 0,
                FetchSucceeded: false,
                FetchReturnedData: false);

        public static EconomicCalendarIngestionResult Failed(long failures, long emptyFetches)
            => new(
                FetchedCount: 0,
                CreatedCount: 0,
                MissingExternalKeyCount: 0,
                CircuitBreakerSkipped: false,
                ConsecutiveFeedFailures: failures,
                ConsecutiveEmptyFetches: emptyFetches,
                FetchSucceeded: false,
                FetchReturnedData: false);

        public static EconomicCalendarIngestionResult Empty(long failures, long emptyFetches)
            => new(
                FetchedCount: 0,
                CreatedCount: 0,
                MissingExternalKeyCount: 0,
                CircuitBreakerSkipped: false,
                ConsecutiveFeedFailures: failures,
                ConsecutiveEmptyFetches: emptyFetches,
                FetchSucceeded: true,
                FetchReturnedData: true);
    }

    private readonly record struct EconomicCalendarActualPatchResult(
        int PendingCount,
        int PatchedCount,
        int InlineActualCandidateCount,
        bool HasPendingHighImpactActuals)
    {
        public static readonly EconomicCalendarActualPatchResult Empty = new(0, 0, 0, false);
    }

    private readonly record struct FeedAlertSyncResult(int DispatchedCount, int ResolvedCount);

    private readonly record struct CircuitBreakerState(bool SkipFetch, bool ProbeAttempt, long ConsecutiveFailures)
    {
        public static CircuitBreakerState Closed(long failures) => new(false, false, failures);
        public static CircuitBreakerState Probe(long failures) => new(false, true, failures);
        public static CircuitBreakerState Open(long failures) => new(true, false, failures);
    }

    private readonly record struct ExistingEconomicEventProjection(
        string Title,
        string Currency,
        DateTime ScheduledAt,
        string? ExternalKey);

    private readonly record struct NormalizedEconomicCalendarEvent(
        string Title,
        string Currency,
        EconomicImpact Impact,
        DateTime ScheduledAt,
        string? Forecast,
        string? Previous,
        string? Actual,
        string? ExternalKey,
        EconomicEventSource Source);
}
