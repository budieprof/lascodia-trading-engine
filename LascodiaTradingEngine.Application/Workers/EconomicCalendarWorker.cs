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
/// <b>Ingestion pass</b> — runs every 6 hours. Fetches events scheduled in the next
/// <see cref="LookaheadDays"/> days for all currencies derived from active
/// <see cref="CurrencyPair"/> records, then inserts any that are not already in the DB
/// (deduplication on Title + Currency + ScheduledAt).
///
/// <b>Actual patch pass</b> — also runs every 6 hours, immediately after ingestion.
/// Finds events whose <c>ScheduledAt</c> is in the past and whose <c>Actual</c> field is
/// still null, then queries the feed for the released figure and patches the record via
/// <see cref="UpdateEconomicEventActualCommand"/>.
///
/// Replace the default <see cref="StubEconomicCalendarFeed"/> registration with a real
/// implementation of <see cref="IEconomicCalendarFeed"/> to switch to live data.
/// </remarks>
public class EconomicCalendarWorker : BackgroundService
{
    private readonly ILogger<EconomicCalendarWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>How often the worker wakes up to sync calendar data.</summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(6);

    /// <summary>How far ahead (in days) to fetch upcoming events on each pass.</summary>
    private const int LookaheadDays = 7;

    public EconomicCalendarWorker(
        ILogger<EconomicCalendarWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EconomicCalendarWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IngestUpcomingEventsAsync(stoppingToken);
                await PatchReleasedActualsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in EconomicCalendarWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("EconomicCalendarWorker stopped");
    }

    // ── Ingestion pass ────────────────────────────────────────────────────────

    private async Task IngestUpcomingEventsAsync(CancellationToken ct)
    {
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
        var toUtc   = fromUtc.AddDays(LookaheadDays);

        IReadOnlyList<Common.Interfaces.EconomicCalendarEvent> incoming;
        try
        {
            incoming = await calendarFeed.GetUpcomingEventsAsync(currencies, fromUtc, toUtc, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EconomicCalendarWorker: failed to fetch upcoming events from feed");
            return;
        }

        if (incoming.Count == 0)
        {
            _logger.LogDebug("EconomicCalendarWorker: no upcoming events returned by feed");
            return;
        }

        // Load existing keys to avoid duplicates (Title + Currency + ScheduledAt, minute precision)
        var existingKeys = await readContext.GetDbContext()
            .Set<EconomicEvent>()
            .Where(e => !e.IsDeleted && e.ScheduledAt >= fromUtc && e.ScheduledAt <= toUtc)
            .Select(e => new { e.Title, e.Currency, e.ScheduledAt })
            .ToListAsync(ct);

        var existingSet = existingKeys
            .Select(e => DedupeKey(e.Title, e.Currency, e.ScheduledAt))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created = 0;
        foreach (var ev in incoming)
        {
            if (existingSet.Contains(DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt)))
                continue;

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
                    Previous    = ev.Previous
                }, ct);

                existingSet.Add(DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt));
                created++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EconomicCalendarWorker: failed to create event '{Title}' ({Currency})", ev.Title, ev.Currency);
            }
        }

        _logger.LogInformation(
            "EconomicCalendarWorker: ingestion pass complete — {Created} new events created out of {Total} fetched",
            created, incoming.Count);
    }

    // ── Actual patch pass ─────────────────────────────────────────────────────

    private async Task PatchReleasedActualsAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();
        var calendarFeed  = scope.ServiceProvider.GetRequiredService<IEconomicCalendarFeed>();

        // Events past their scheduled time but still missing an actual value
        var pending = await readContext.GetDbContext()
            .Set<EconomicEvent>()
            .Where(e => !e.IsDeleted && e.Actual == null && e.ScheduledAt < DateTime.UtcNow)
            .OrderBy(e => e.ScheduledAt)
            .Take(50)   // cap per cycle to limit API calls
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
                var externalKey = DedupeKey(ev.Title, ev.Currency, ev.ScheduledAt);
                var actual = await calendarFeed.GetActualAsync(externalKey, ct);

                if (actual is null)
                    continue;

                await mediator.Send(new UpdateEconomicEventActualCommand
                {
                    Id     = ev.Id,
                    Actual = actual
                }, ct);

                patched++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EconomicCalendarWorker: failed to patch actual for event id={Id}", ev.Id);
            }
        }

        _logger.LogInformation(
            "EconomicCalendarWorker: patched actuals for {Patched}/{Total} events", patched, pending.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<List<string>> GetActiveCurrenciesAsync(
        IReadApplicationDbContext readContext, CancellationToken ct)
    {
        var symbols = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => x.Symbol)
            .ToListAsync(ct);

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
    private static string DedupeKey(string title, string currency, DateTime scheduledAt)
        => $"{title.Trim().ToUpperInvariant()}|{currency.ToUpperInvariant()}|{scheduledAt:yyyyMMddHHmm}";
}
