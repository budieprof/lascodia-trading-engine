using System.Diagnostics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.CreateEconomicEvent;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.UpdateEconomicEventActual;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that keeps the <see cref="EconomicEvent"/> table in sync with an
/// external economic calendar feed. Runs two passes per cycle — ingestion of upcoming
/// events and patching of released actuals — with adaptive polling, weekend skipping,
/// and a circuit breaker for sustained feed failures.
/// </summary>
/// <seealso cref="EconomicCalendarOptions"/>
/// <seealso cref="IEconomicCalendarFeed"/>
/// <seealso cref="EconomicEvent"/>
public class EconomicCalendarWorker : BackgroundService
{
    private readonly ILogger<EconomicCalendarWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EconomicCalendarOptions _options;
    private readonly TradingMetrics _metrics;

    private long _consecutiveEmptyFetches;
    private long _consecutiveFeedFailures;

    public EconomicCalendarWorker(
        ILogger<EconomicCalendarWorker> logger,
        IServiceScopeFactory scopeFactory,
        EconomicCalendarOptions options,
        TradingMetrics metrics)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
        _metrics      = metrics;

        _metrics.RegisterEconEmptyFetchGauge(() => Interlocked.Read(ref _consecutiveEmptyFetches));
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure.
    /// Runs a continuous polling loop, executing both the ingestion and actuals-patch
    /// passes on each cycle before sleeping for the configured (or adaptive) interval.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "EconomicCalendarWorker starting (interval={Interval}h, lookahead={Lookahead}d, stale cutoff={Cutoff}d, batch={Batch}, feedTimeout={Timeout}s, retries={Retries}, patchConcurrency={Concurrency}, patchRetries={PatchRetries}, skipWeekends={SkipWeekends}, circuitBreaker={CircuitBreaker})",
            _options.PollingIntervalHours, _options.LookaheadDays, _options.StaleEventCutoffDays,
            _options.ActualsPatchBatchSize, _options.FeedCallTimeoutSeconds, _options.FeedRetryCount,
            _options.ActualsPatchMaxConcurrency, _options.ActualsPatchRetryCount, _options.SkipWeekends,
            _options.FeedCircuitBreakerThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            // ── Weekend skip ─────────────────────────────────────────────────
            if (_options.SkipWeekends && IsWeekend())
            {
                _logger.LogDebug("EconomicCalendarWorker: skipping cycle (weekend)");
                await Task.Delay(TimeSpan.FromHours(_options.PollingIntervalHours), stoppingToken);
                continue;
            }

            var cycleSw = Stopwatch.StartNew();
            var hadPendingActuals = false;

            try
            {
                await IngestUpcomingEventsAsync(stoppingToken);
                hadPendingActuals = await PatchReleasedActualsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in EconomicCalendarWorker polling loop");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "EconomicCalendar"),
                    new KeyValuePair<string, object?>("reason", "unhandled"));
            }

            cycleSw.Stop();
            _metrics.EconCycleDurationMs.Record(cycleSw.Elapsed.TotalMilliseconds);

            // ── Adaptive interval (skip DB query when no pending actuals) ────
            var nextInterval = hadPendingActuals
                ? await ComputeNextIntervalAsync(stoppingToken)
                : TimeSpan.FromHours(_options.PollingIntervalHours);

            await Task.Delay(nextInterval, stoppingToken);
        }

        _logger.LogInformation("EconomicCalendarWorker stopped");
    }

    // ── Ingestion pass ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches upcoming economic events from the configured calendar feed for the next
    /// N days and inserts any new events that are not already present in the database.
    /// Retries transient feed errors with exponential backoff.
    /// Deduplicates on <c>ExternalKey</c> first, falling back to composite key.
    /// Skips when the feed circuit breaker is open.
    /// </summary>
    private async Task IngestUpcomingEventsAsync(CancellationToken ct)
    {
        // ── Circuit breaker ──────────────────────────────────────────────────
        var failures = Interlocked.Read(ref _consecutiveFeedFailures);
        if (failures >= _options.FeedCircuitBreakerThreshold)
        {
            _logger.LogWarning(
                "EconomicCalendarWorker: feed circuit breaker open ({Failures} consecutive failures, threshold={Threshold}) — skipping ingestion",
                failures, _options.FeedCircuitBreakerThreshold);

            // Probe fires when failure count is an exact multiple of the threshold
            // (e.g. threshold=3: skip at 4,5 → probe at 6 → skip at 7,8 → probe at 9 …)
            if (failures % _options.FeedCircuitBreakerThreshold != 0)
            {
                Interlocked.Increment(ref _consecutiveFeedFailures);
                return;
            }

            _logger.LogInformation("EconomicCalendarWorker: circuit breaker probe — attempting ingestion");
        }

        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();
        var calendarFeed  = scope.ServiceProvider.GetRequiredService<IEconomicCalendarFeed>();

        var currencies = await GetActiveCurrenciesAsync(readContext, ct);
        if (currencies.Count == 0)
        {
            _logger.LogDebug("EconomicCalendarWorker: no active currency pairs, skipping ingestion");
            return;
        }

        var fromUtc = DateTime.UtcNow;
        var toUtc   = fromUtc.AddDays(_options.LookaheadDays);

        var incoming = await TryFetchUpcomingEventsAsync(calendarFeed, currencies, fromUtc, toUtc, ct);
        if (incoming is null)
            return; // All retries exhausted — circuit breaker already incremented

        if (incoming.Count == 0)
        {
            Interlocked.Increment(ref _consecutiveEmptyFetches);
            if (_consecutiveEmptyFetches >= _options.SustainedEmptyFetchThreshold)
            {
                _logger.LogCritical(
                    "EconomicCalendarWorker: feed returned 0 events for {Count} consecutive cycles — possible feed structural change or blocking. Check EconFeedParseFailures metric and ForexFactory page structure",
                    _consecutiveEmptyFetches);
                _metrics.EconFeedErrors.Add(1,
                    new KeyValuePair<string, object?>("phase", "ingestion"),
                    new KeyValuePair<string, object?>("reason", "sustained_empty"));
            }
            else
            {
                _logger.LogDebug("EconomicCalendarWorker: no upcoming events returned by feed");
            }
            return;
        }

        // Reset empty-fetch counter on a successful non-empty response
        Interlocked.Exchange(ref _consecutiveEmptyFetches, 0);

        // ── Build deduplication sets (single query, two indices) ──────────────
        var existingEvents = await readContext.GetDbContext()
            .Set<EconomicEvent>()
            .Where(e => !e.IsDeleted && e.ScheduledAt >= fromUtc && e.ScheduledAt <= toUtc)
            .Select(e => new { e.Title, e.Currency, e.ScheduledAt, e.ExternalKey })
            .ToListAsync(ct);

        var externalKeySet = existingEvents
            .Where(e => e.ExternalKey != null)
            .Select(e => e.ExternalKey!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var compositeKeySet = existingEvents
            .Select(e => DedupeKey(e.Title, e.Currency, e.ScheduledAt))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created = 0;
        int missingExternalKey = 0;

        foreach (var ev in incoming)
        {
            // Primary dedup: ExternalKey (more reliable across feed title variations)
            if (!string.IsNullOrWhiteSpace(ev.ExternalKey) && externalKeySet.Contains(ev.ExternalKey))
                continue;

            // Fallback dedup: composite key (Title + Currency + ScheduledAt at minute precision)
            if (compositeKeySet.Contains(DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt)))
                continue;

            // Warn if no ExternalKey — actuals patching will be unavailable for this event
            if (string.IsNullOrWhiteSpace(ev.ExternalKey))
            {
                missingExternalKey++;
                _logger.LogWarning(
                    "EconomicCalendarWorker: event '{Title}' ({Currency} at {ScheduledAt:u}) has no ExternalKey — actuals patching will be unavailable",
                    ev.Title, ev.Currency, ev.ScheduledAt);
            }

            try
            {
                await mediator.Send(new CreateEconomicEventCommand
                {
                    Title       = ev.Title,
                    Currency    = ev.Currency,
                    Impact      = ev.Impact.ToString(),
                    ScheduledAt = ev.ScheduledAt,
                    Source      = ev.Source.ToString(),
                    Forecast    = ev.Forecast,
                    Previous    = ev.Previous,
                    Actual      = ev.Actual,
                    ExternalKey = ev.ExternalKey
                }, ct);

                // Update both dedup sets
                if (!string.IsNullOrWhiteSpace(ev.ExternalKey))
                    externalKeySet.Add(ev.ExternalKey);
                compositeKeySet.Add(DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt));
                created++;
            }
            catch (Exception ex)
            {
                _metrics.EconFeedErrors.Add(1, new KeyValuePair<string, object?>("phase", "ingestion_persist"), new KeyValuePair<string, object?>("reason", "db_error"));
                _logger.LogError(ex,
                    "EconomicCalendarWorker: failed to create event '{Title}' ({Currency})", ev.Title, ev.Currency);
            }
        }

        _metrics.EconEventsIngested.Add(created);
        _logger.LogInformation(
            "EconomicCalendarWorker: ingestion pass complete — {Created} new events created out of {Total} fetched ({MissingKey} without ExternalKey)",
            created, incoming.Count, missingExternalKey);

        // ── Audit trail for ingestion pass ────────────────────────────────────
        if (created > 0)
        {
            await TryLogDecisionAsync(mediator,
                entityType:   "EconomicCalendar",
                entityId:     0,
                decisionType: "Ingestion",
                outcome:      "Completed",
                reason:       $"Ingested {created} of {incoming.Count} events for {currencies.Count} currencies ({fromUtc:u} to {toUtc:u}), {missingExternalKey} without ExternalKey",
                ct);
        }
    }

    // ── Actual patch pass ─────────────────────────────────────────────────────

    /// <summary>
    /// Finds past economic events that are still missing their released actual value and
    /// attempts to patch them by querying the calendar feed using the event's stored
    /// <see cref="EconomicEvent.ExternalKey"/>. Events older than
    /// <see cref="EconomicCalendarOptions.StaleEventCutoffDays"/> are skipped.
    /// High-impact events are prioritised. Fetches run concurrently up to
    /// <see cref="EconomicCalendarOptions.ActualsPatchMaxConcurrency"/> and each attempt
    /// retries up to <see cref="EconomicCalendarOptions.ActualsPatchRetryCount"/> times.
    /// Returns true if there were pending events, so the main loop can decide whether
    /// the adaptive interval DB query is worth running.
    /// </summary>
    private async Task<bool> PatchReleasedActualsAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var staleCutoff = DateTime.UtcNow.AddDays(-_options.StaleEventCutoffDays);

        // Impact-based prioritisation: High-impact events are patched first
        var pending = await readContext.GetDbContext()
            .Set<EconomicEvent>()
            .Where(e => !e.IsDeleted
                     && e.Actual == null
                     && e.ExternalKey != null
                     && e.ScheduledAt < DateTime.UtcNow
                     && e.ScheduledAt >= staleCutoff)
            .OrderByDescending(e => e.Impact)
            .ThenBy(e => e.ScheduledAt)
            .Take(_options.ActualsPatchBatchSize)
            .Select(e => new { e.Id, e.ExternalKey, e.Impact })
            .ToListAsync(ct);

        if (pending.Count == 0)
            return false;

        _logger.LogInformation(
            "EconomicCalendarWorker: patching actuals for {Count} past events (high-impact first)", pending.Count);

        // Parallel execution with per-event scopes (DbContext is not thread-safe)
        int patched = 0;
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.ActualsPatchMaxConcurrency,
                CancellationToken = ct
            },
            async (ev, token) =>
            {
                try
                {
                    using var innerScope = _scopeFactory.CreateScope();
                    var innerMediator    = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                    var innerFeed        = innerScope.ServiceProvider.GetRequiredService<IEconomicCalendarFeed>();

                    if (await TryFetchAndPatchActualAsync(ev.Id, ev.ExternalKey!, innerFeed, innerMediator, token))
                        Interlocked.Increment(ref patched);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "EconomicCalendarWorker: failed to patch actual for event {EventId} — isolated, continuing batch",
                        ev.Id);
                    _metrics.EconFeedErrors.Add(1,
                        new KeyValuePair<string, object?>("reason", "actual_patch_failed"),
                        new KeyValuePair<string, object?>("event_id", ev.Id.ToString()));
                }
            });

        _metrics.EconActualsPatched.Add(patched);
        _logger.LogInformation(
            "EconomicCalendarWorker: patched actuals for {Patched}/{Total} events", patched, pending.Count);

        // ── Audit trail for actuals patch pass ────────────────────────────────
        if (patched > 0)
        {
            using var auditScope = _scopeFactory.CreateScope();
            var auditMediator    = auditScope.ServiceProvider.GetRequiredService<IMediator>();

            await TryLogDecisionAsync(auditMediator,
                entityType:   "EconomicCalendar",
                entityId:     0,
                decisionType: "ActualsPatch",
                outcome:      "Completed",
                reason:       $"Patched actuals for {patched} of {pending.Count} events (stale cutoff={staleCutoff:u})",
                ct);
        }

        return true;
    }

    /// <summary>
    /// Fetches upcoming events from the calendar feed with retry and exponential backoff.
    /// Returns null (and increments the circuit breaker) if all retries are exhausted.
    /// Resets the circuit breaker on a successful fetch.
    /// </summary>
    private async Task<IReadOnlyList<EconomicCalendarEvent>?> TryFetchUpcomingEventsAsync(
        IEconomicCalendarFeed calendarFeed, List<string> currencies,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        int maxAttempts = 1 + _options.FeedRetryCount;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.FeedCallTimeoutSeconds));

                var result = await calendarFeed.GetUpcomingEventsAsync(currencies, fromUtc, toUtc, timeoutCts.Token);

                // Successful fetch — reset circuit breaker
                Interlocked.Exchange(ref _consecutiveFeedFailures, 0);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Host shutdown — propagate
            }
            catch (OperationCanceledException)
            {
                _metrics.EconFeedErrors.Add(1, new KeyValuePair<string, object?>("phase", "ingestion"), new KeyValuePair<string, object?>("reason", "timeout"));
                _logger.LogWarning(
                    "EconomicCalendarWorker: feed timeout on ingestion attempt {Attempt}/{Max} ({Timeout}s)",
                    attempt, maxAttempts, _options.FeedCallTimeoutSeconds);

                if (attempt >= maxAttempts)
                {
                    Interlocked.Increment(ref _consecutiveFeedFailures);
                    return null;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _metrics.EconFeedErrors.Add(1, new KeyValuePair<string, object?>("phase", "ingestion"), new KeyValuePair<string, object?>("reason", "transient"));
                _logger.LogWarning(ex,
                    "EconomicCalendarWorker: transient feed error on ingestion attempt {Attempt}/{Max} — retrying",
                    attempt, maxAttempts);

                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
            }
            catch (Exception ex)
            {
                _metrics.EconFeedErrors.Add(1, new KeyValuePair<string, object?>("phase", "ingestion"), new KeyValuePair<string, object?>("reason", "exhausted"));
                _logger.LogError(ex,
                    "EconomicCalendarWorker: feed error on final ingestion attempt {Attempt}/{Max} — skipping cycle",
                    attempt, maxAttempts);

                Interlocked.Increment(ref _consecutiveFeedFailures);
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to fetch and patch the actual value for a single economic event,
    /// with retry and exponential backoff on transient errors.
    /// </summary>
    private async Task<bool> TryFetchAndPatchActualAsync(
        long eventId, string externalKey,
        IEconomicCalendarFeed calendarFeed, IMediator mediator,
        CancellationToken ct)
    {
        int maxAttempts = 1 + _options.ActualsPatchRetryCount;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.FeedCallTimeoutSeconds));

                var actual = await calendarFeed.GetActualAsync(externalKey, timeoutCts.Token);

                if (actual is null)
                    return false;

                await mediator.Send(new UpdateEconomicEventActualCommand
                {
                    Id     = eventId,
                    Actual = actual
                }, ct);

                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Host shutdown — propagate
            }
            catch (OperationCanceledException)
            {
                _metrics.EconFeedErrors.Add(1,
                    new KeyValuePair<string, object?>("phase", "actuals_patch"),
                    new KeyValuePair<string, object?>("reason", "timeout"));
                _logger.LogWarning(
                    "EconomicCalendarWorker: timeout fetching actual for event id={Id}, attempt {Attempt}/{Max} ({Timeout}s)",
                    eventId, attempt, maxAttempts, _options.FeedCallTimeoutSeconds);

                if (attempt >= maxAttempts) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
            }
            catch (Exception ex)
            {
                _metrics.EconFeedErrors.Add(1,
                    new KeyValuePair<string, object?>("phase", "actuals_patch"),
                    new KeyValuePair<string, object?>("reason", "error"));
                _logger.LogWarning(ex,
                    "EconomicCalendarWorker: error fetching actual for event id={Id}, attempt {Attempt}/{Max}",
                    eventId, attempt, maxAttempts);

                if (attempt >= maxAttempts) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
            }
        }

        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries the read database for all active currency pairs and extracts their
    /// <c>BaseCurrency</c> and <c>QuoteCurrency</c> fields into a deduplicated list
    /// of ISO currency codes.
    /// </summary>
    private static async Task<List<string>> GetActiveCurrenciesAsync(
        IReadApplicationDbContext readContext, CancellationToken ct)
    {
        // Load base/quote pairs first, then flatten client-side.
        // EF Core cannot translate SelectMany with array construction.
        var pairs = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => new { x.BaseCurrency, x.QuoteCurrency })
            .ToListAsync(ct);

        return pairs
            .SelectMany(x => new[] { x.BaseCurrency, x.QuoteCurrency })
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Computes the next polling interval. Returns a shorter interval when recently-released
    /// high-impact events still lack their actual values, allowing faster post-release patching.
    /// Only called when the previous cycle found pending actuals to avoid unnecessary DB queries.
    /// </summary>
    private async Task<TimeSpan> ComputeNextIntervalAsync(CancellationToken ct)
    {
        var baseInterval = TimeSpan.FromHours(_options.PollingIntervalHours);

        try
        {
            using var scope    = _scopeFactory.CreateScope();
            var readContext    = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var now            = DateTime.UtcNow;

            var hasPendingHighImpactActuals = await readContext.GetDbContext()
                .Set<EconomicEvent>()
                .AnyAsync(e => !e.IsDeleted
                    && e.Impact == EconomicImpact.High
                    && e.Actual == null
                    && e.ExternalKey != null
                    && e.ScheduledAt < now
                    && e.ScheduledAt >= now.AddHours(-2), ct);

            if (hasPendingHighImpactActuals)
            {
                var accelerated = TimeSpan.FromHours(Math.Max(1, _options.PollingIntervalHours / 4.0));
                _logger.LogInformation(
                    "EconomicCalendarWorker: high-impact events pending actuals — using accelerated interval {Interval}h",
                    accelerated.TotalHours);
                return accelerated;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EconomicCalendarWorker: failed to compute adaptive interval — using default");
        }

        return baseInterval;
    }

    /// <summary>
    /// Returns true if the current UTC day is Saturday or Sunday. Economic releases are
    /// almost never scheduled on weekends, so polling can be skipped to reduce unnecessary
    /// feed API calls.
    /// </summary>
    private static bool IsWeekend()
    {
        return DateTime.UtcNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    /// <summary>
    /// Best-effort audit trail log. Failures are logged but do not propagate — the worker
    /// must not fail because an audit record could not be written.
    /// </summary>
    private async Task TryLogDecisionAsync(
        IMediator mediator, string entityType, long entityId,
        string decisionType, string outcome, string reason, CancellationToken ct)
    {
        try
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = entityType,
                EntityId     = entityId,
                DecisionType = decisionType,
                Outcome      = outcome,
                Reason       = reason,
                Source       = nameof(EconomicCalendarWorker)
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EconomicCalendarWorker: failed to write audit trail ({DecisionType}/{Outcome})",
                decisionType, outcome);
        }
    }

    /// <summary>
    /// Produces a stable deduplication key from the event identity fields,
    /// truncating the timestamp to minute precision to tolerate minor feed discrepancies.
    /// </summary>
    private static string DedupeKey(string title, string currency, DateTime scheduledAt)
        => $"{title.Trim().ToUpperInvariant()}|{currency.ToUpperInvariant()}|{scheduledAt:yyyyMMddHHmm}";
}
