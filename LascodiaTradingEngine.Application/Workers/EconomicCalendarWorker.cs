using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.CreateEconomicEvent;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.UpdateEconomicEventActual;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that keeps the <see cref="EconomicEvent"/> table in sync with an
/// external economic calendar (ForexFactory, Investing.com, or a configured stub).
/// </summary>
/// <remarks>
/// <b>Role in the trading engine:</b>
/// High-impact economic releases (NFP, CPI, central bank rate decisions) can trigger
/// extreme volatility and cause strategy signals to behave unpredictably. The engine uses
/// the event database to apply a news blackout window: the <c>NewsFilter</c> and
/// <c>EconomicCalendarWorker</c> together ensure that no new positions are opened within
/// a configurable window before and after a high-impact event. Post-release, the actual
/// figure is used to contextualise ML feature sets for backtesting and training.
///
/// <b>Two-phase operation — Ingestion + Actuals patch:</b>
/// Each polling cycle performs two sequential passes:
/// <list type="number">
///   <item>
///     <b>Ingestion pass</b> (<see cref="IngestUpcomingEventsAsync"/>) — fetches events
///     scheduled in the next <see cref="LookaheadDays"/> days for all currencies derived
///     from active <see cref="CurrencyPair"/> records, then inserts any that are not
///     already present. Deduplication uses a composite key of Title + Currency + ScheduledAt
///     (truncated to minute precision) to tolerate minor timestamp variations between feed
///     updates without creating duplicate rows.
///   </item>
///   <item>
///     <b>Actuals patch pass</b> (<see cref="PatchReleasedActualsAsync"/>) — finds events
///     whose <c>ScheduledAt</c> is in the past and whose <c>Actual</c> field is still null,
///     then queries the feed for the released figure and patches the record via
///     <see cref="UpdateEconomicEventActualCommand"/>. This two-phase design allows the
///     engine to track whether a release has occurred and what the actual number was,
///     which is important for post-trade analysis and ML feature enrichment.
///   </item>
/// </list>
///
/// <b>Polling cadence:</b>
/// Runs every 6 hours (<see cref="PollingInterval"/>). Economic calendars are typically
/// stable a week out and only require intraday updates on the day of release. Six-hour
/// polling gives timely ingestion without hammering the data provider.
///
/// <b>Data source:</b>
/// Resolved from DI as <see cref="IEconomicCalendarFeed"/>. The default registration
/// points to <c>StubEconomicCalendarFeed</c>. Replace with a real implementation
/// (e.g. <c>ForexFactoryCalendarFeed</c>, <c>InvestingComCalendarFeed</c>) by updating
/// the DI registration in <c>Application/DependencyInjection.cs</c>.
///
/// <b>Currency extraction:</b>
/// Economic events are keyed by individual currency codes (e.g. "USD", "EUR") not pair
/// symbols. The worker splits each 6-character pair symbol into its base and quote
/// currency to build the currency filter list passed to the feed.
/// </remarks>
public class EconomicCalendarWorker : BackgroundService
{
    private readonly ILogger<EconomicCalendarWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// How often the worker wakes up to sync calendar data with the external feed.
    /// 6 hours balances timeliness against rate limits on the calendar data provider.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// How far ahead (in days) to fetch upcoming events on each ingestion pass.
    /// 7 days ensures the engine has visibility of the full trading week ahead,
    /// allowing the <c>NewsFilter</c> to pre-screen events at signal evaluation time.
    /// </summary>
    private const int LookaheadDays = 7;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for operational and diagnostic messages.</param>
    /// <param name="scopeFactory">
    /// Factory used to create short-lived DI scopes for each polling cycle pass.
    /// Separate scopes are created for the ingestion and actuals-patch passes to ensure
    /// clean DbContext lifetimes and avoid stale tracked entities between the two operations.
    /// </param>
    public EconomicCalendarWorker(
        ILogger<EconomicCalendarWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure.
    /// Runs a continuous polling loop, executing both the ingestion and actuals-patch
    /// passes on each cycle before sleeping for <see cref="PollingInterval"/>.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EconomicCalendarWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run the ingestion pass first to ensure new upcoming events are stored
                // before attempting to patch actuals (which only patches existing rows).
                await IngestUpcomingEventsAsync(stoppingToken);
                await PatchReleasedActualsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during graceful shutdown — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Catch-all so a transient error in either pass does not kill the worker.
                // Both passes will be retried on the next cycle.
                _logger.LogError(ex, "Unexpected error in EconomicCalendarWorker polling loop");
            }

            // Wait before the next full sync cycle. Task.Delay respects cancellation
            // so the worker shuts down promptly even when mid-sleep.
            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("EconomicCalendarWorker stopped");
    }

    // ── Ingestion pass ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches upcoming economic events from the configured calendar feed for the next
    /// <see cref="LookaheadDays"/> days and inserts any new events that are not already
    /// present in the database.
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <remarks>
    /// <b>Deduplication strategy:</b>
    /// Rather than issuing a DB query per incoming event, the method loads all existing
    /// event identity keys within the lookahead window into a <see cref="HashSet{T}"/> in
    /// a single query. Membership checks against this set are O(1), making the ingestion
    /// pass efficient even for feeds returning hundreds of events.
    ///
    /// After each successful insert the key is added to <c>existingSet</c> in memory so
    /// that duplicate entries within the same feed response are also suppressed without
    /// requiring additional DB round-trips.
    /// </remarks>
    private async Task IngestUpcomingEventsAsync(CancellationToken ct)
    {
        // Use a dedicated scope for the ingestion pass so the read and write contexts
        // are freshly allocated and do not carry stale entity tracking state from prior cycles.
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();
        var calendarFeed  = scope.ServiceProvider.GetRequiredService<IEconomicCalendarFeed>();

        // Derive the currency filter list from currently active pairs.
        var currencies = await GetActiveCurrenciesAsync(readContext, ct);
        if (currencies.Count == 0)
        {
            _logger.LogDebug("EconomicCalendarWorker: no active currency pairs, skipping ingestion");
            return;
        }

        // Define the lookahead window: now → now + 7 days.
        var fromUtc = DateTime.UtcNow;
        var toUtc   = fromUtc.AddDays(LookaheadDays);

        IReadOnlyList<Common.Interfaces.EconomicCalendarEvent> incoming;
        try
        {
            // Fetch the full event list for all relevant currencies within the time window.
            // The feed is responsible for filtering by impact level if configured to do so.
            incoming = await calendarFeed.GetUpcomingEventsAsync(currencies, fromUtc, toUtc, ct);
        }
        catch (Exception ex)
        {
            // Feed errors are logged but do not propagate — the current DB state is preserved
            // and the next cycle will attempt a fresh fetch.
            _logger.LogError(ex, "EconomicCalendarWorker: failed to fetch upcoming events from feed");
            return;
        }

        if (incoming.Count == 0)
        {
            _logger.LogDebug("EconomicCalendarWorker: no upcoming events returned by feed");
            return;
        }

        // Pre-load the deduplication set from the DB in a single query.
        // Scoped to the same time window to avoid loading the entire event history.
        // Load existing keys to avoid duplicates (Title + Currency + ScheduledAt, minute precision)
        var existingKeys = await readContext.GetDbContext()
            .Set<EconomicEvent>()
            .Where(e => !e.IsDeleted && e.ScheduledAt >= fromUtc && e.ScheduledAt <= toUtc)
            .Select(e => new { e.Title, e.Currency, e.ScheduledAt })
            .ToListAsync(ct);

        // Build the hash set for O(1) deduplication. OrdinalIgnoreCase handles any
        // minor casing differences between the DB and the incoming feed data.
        var existingSet = existingKeys
            .Select(e => DedupeKey(e.Title, e.Currency, e.ScheduledAt))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created = 0;
        foreach (var ev in incoming)
        {
            // Skip events already present in the DB (or already created in this batch).
            if (existingSet.Contains(DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt)))
                continue;

            try
            {
                // Route through MediatR so the full pipeline (validation, soft-delete check,
                // event publishing) is applied — same as a manual API call.
                await mediator.Send(new CreateEconomicEventCommand
                {
                    Title       = ev.Title,
                    Currency    = ev.Currency,
                    Impact      = ev.Impact.ToString(),
                    ScheduledAt = ev.ScheduledAt,
                    Source      = ev.Source.ToString(),
                    Forecast    = ev.Forecast,   // analyst consensus estimate at time of ingestion
                    Previous    = ev.Previous    // prior period's released figure
                }, ct);

                // Add to the in-memory set so subsequent duplicates in the same feed response
                // are also suppressed without needing another DB query.
                existingSet.Add(DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt));
                created++;
            }
            catch (Exception ex)
            {
                // Per-event isolation: one failed insert does not abort the rest of the batch.
                _logger.LogError(ex,
                    "EconomicCalendarWorker: failed to create event '{Title}' ({Currency})", ev.Title, ev.Currency);
            }
        }

        _logger.LogInformation(
            "EconomicCalendarWorker: ingestion pass complete — {Created} new events created out of {Total} fetched",
            created, incoming.Count);
    }

    // ── Actual patch pass ─────────────────────────────────────────────────────

    /// <summary>
    /// Finds past economic events that are still missing their released actual value and
    /// attempts to patch them by querying the calendar feed for the released figure.
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <remarks>
    /// <b>Why actuals matter:</b>
    /// The actual released figure, compared against the forecast and previous period,
    /// determines whether the event was a positive or negative surprise. This surprise
    /// magnitude is used as a feature in ML models and in post-trade attribution to
    /// explain unusual price behaviour around high-impact releases.
    ///
    /// <b>Batch cap:</b>
    /// The query is capped at 50 events per cycle (<c>Take(50)</c>) to limit the number
    /// of individual API calls made to the calendar feed in a single pass. Events are
    /// processed oldest-first so older releases are resolved before newer ones in backlogs.
    ///
    /// <b>Null handling:</b>
    /// If the feed returns null for a given event (actual not yet published by the source),
    /// the record is left untouched and will be retried on the next polling cycle.
    /// </remarks>
    private async Task PatchReleasedActualsAsync(CancellationToken ct)
    {
        // Use a separate scope from the ingestion pass to avoid carrying over tracked
        // entities from CreateEconomicEventCommand writes.
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();
        var calendarFeed  = scope.ServiceProvider.GetRequiredService<IEconomicCalendarFeed>();

        // Query for events that have passed their scheduled time but still have no actual value.
        // Events past their scheduled time but still missing an actual value
        var pending = await readContext.GetDbContext()
            .Set<EconomicEvent>()
            .Where(e => !e.IsDeleted && e.Actual == null && e.ScheduledAt < DateTime.UtcNow)
            .OrderBy(e => e.ScheduledAt)   // oldest first — resolve backlog in chronological order
            .Take(50)                       // cap per cycle to limit API calls to the calendar feed
            .Select(e => new { e.Id, e.Title, e.Currency, e.ScheduledAt })
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        _logger.LogInformation(
            "EconomicCalendarWorker: patching actuals for {Count} past events", pending.Count);

        int patched = 0;
        foreach (var ev in pending)
        {
            try
            {
                // Reconstruct the same stable key used during ingestion so the feed can
                // look up the correct event record in its own storage.
                var externalKey = DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt);
                var actual = await calendarFeed.GetActualAsync(externalKey, ct);

                // null means the feed does not yet have the actual figure (e.g. the release
                // is delayed or the feed has not yet processed it). Skip without error —
                // the next polling cycle will retry.
                if (actual is null)
                    continue;

                // Patch the actual value through MediatR to ensure the update goes through
                // the full pipeline (optimistic concurrency check, audit trail, etc.).
                await mediator.Send(new UpdateEconomicEventActualCommand
                {
                    Id     = ev.Id,
                    Actual = actual
                }, ct);

                patched++;
            }
            catch (Exception ex)
            {
                // Per-event isolation: one failed patch does not abort the rest of the batch.
                _logger.LogError(ex,
                    "EconomicCalendarWorker: failed to patch actual for event id={Id}", ev.Id);
            }
        }

        _logger.LogInformation(
            "EconomicCalendarWorker: patched actuals for {Patched}/{Total} events", patched, pending.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries the read database for all active currency pair symbols and expands each
    /// 6-character symbol into its constituent base and quote currency codes.
    /// </summary>
    /// <param name="readContext">Read DbContext for querying active pairs.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <returns>
    /// A deduplicated list of 3-character ISO currency codes (e.g. <c>["USD", "EUR", "GBP"]</c>)
    /// derived from all active pairs. Used as the currency filter when calling
    /// <see cref="IEconomicCalendarFeed.GetUpcomingEventsAsync"/>.
    /// </returns>
    /// <remarks>
    /// A standard forex pair symbol (e.g. "EURUSD") encodes both the base currency
    /// (first 3 chars: "EUR") and the quote currency (next 3 chars: "USD"). Splitting
    /// each symbol and deduplicating ensures the engine fetches events for all currencies
    /// it trades in, regardless of whether they appear as base or quote in a given pair.
    /// </remarks>
    private static async Task<List<string>> GetActiveCurrenciesAsync(
        IReadApplicationDbContext readContext, CancellationToken ct)
    {
        var symbols = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => x.Symbol)
            .ToListAsync(ct);

        // Split each 6-char symbol (e.g. "EURUSD") into ["EUR", "USD"].
        // Symbols shorter than 6 chars are treated as a single currency code.
        // Distinct() deduplicates when multiple pairs share a currency (e.g. EUR appears
        // in both EURUSD and EURGBP — we only need EUR events once).
        return symbols
            .SelectMany(s => s.Length >= 6
                ? new[] { s[..3].ToUpperInvariant(), s[3..6].ToUpperInvariant() }
                : new[] { s.ToUpperInvariant() })
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Produces a stable deduplication key from the event identity fields,
    /// truncating the timestamp to minute precision to tolerate minor feed discrepancies.
    /// </summary>
    /// <param name="title">The event title (e.g. "Non-Farm Payrolls").</param>
    /// <param name="currency">The ISO currency code the event belongs to (e.g. "USD").</param>
    /// <param name="scheduledAt">The UTC time the event is scheduled to release.</param>
    /// <returns>
    /// A pipe-delimited key string in the form <c>TITLE|CURRENCY|yyyyMMddHHmm</c>.
    /// </returns>
    /// <remarks>
    /// The timestamp is truncated to minute precision (format <c>yyyyMMddHHmm</c>) because
    /// different feed providers sometimes differ by a few seconds on the exact release time.
    /// Minute-level truncation absorbs these discrepancies while still distinguishing events
    /// that are genuinely scheduled at different times on the same day for the same currency.
    /// </remarks>
    private static string DedupeKey(string title, string currency, DateTime scheduledAt)
        => $"{title.Trim().ToUpperInvariant()}|{currency.ToUpperInvariant()}|{scheduledAt:yyyyMMddHHmm}";
}
