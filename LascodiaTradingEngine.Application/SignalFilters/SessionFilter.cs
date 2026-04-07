using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTradingSessions;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Determines the active trading session based on UTC time and checks
/// whether that session is in the caller's allowed list.
///
/// On first use and every hour thereafter, attempts to load persisted session
/// data from EngineConfig (key pattern: <c>EA:TradingSessions:{InstanceId}</c>)
/// and from the dedicated TradingSessionSchedule table.
/// Falls back to hardcoded UTC ranges when no persisted data exists.
///
/// Default session ranges (UTC):
///   Asian            00:00 – 09:00
///   London           08:00 – 17:00
///   LondonNYOverlap  13:00 – 17:00  (highest priority)
///   NewYork          13:00 – 22:00
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class SessionFilter : ISessionFilter
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionFilter> _logger;

    private readonly object _lock = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private List<TradingSessionItem>? _persistedSessions;
    private readonly ConcurrentDictionary<string, List<SessionScheduleEntry>> _symbolSessions = new(StringComparer.OrdinalIgnoreCase);

    public SessionFilter()
        : this(new NoOpServiceScopeFactory(), NullLogger<SessionFilter>.Instance)
    {
    }

    public SessionFilter(
        IServiceScopeFactory scopeFactory,
        ILogger<SessionFilter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public TradingSession GetCurrentSession(DateTime utcTime)
    {
        RefreshIfStale();

        var sessions = _persistedSessions;
        if (sessions is { Count: > 0 })
        {
            var match = ResolveFromPersisted(utcTime, sessions);
            if (match is not null) return match.Value;
        }

        return GetCurrentSessionFromDefaults(utcTime);
    }

    public TradingSession? GetCurrentSession(DateTime utcTime, string symbol)
    {
        RefreshIfStale();

        // Check for symbol-specific session schedule first
        if (!string.IsNullOrWhiteSpace(symbol)
            && _symbolSessions.TryGetValue(symbol, out var symbolSchedules)
            && symbolSchedules.Count > 0)
        {
            var currentTimeOfDay = utcTime.TimeOfDay;
            var currentDayOfWeek = (int)utcTime.DayOfWeek;

            TradingSession? bestMatch = null;
            int bestPriority = int.MaxValue;

            foreach (var schedule in symbolSchedules)
            {
                if (currentDayOfWeek >= schedule.DayOfWeekStart
                    && currentDayOfWeek <= schedule.DayOfWeekEnd
                    && currentTimeOfDay >= schedule.OpenTime
                    && currentTimeOfDay < schedule.CloseTime
                    && TryParseSession(schedule.SessionName, out var session))
                {
                    var priority = GetSessionPriority(session);
                    if (priority < bestPriority)
                    {
                        bestMatch = session;
                        bestPriority = priority;
                    }
                }
            }

            // Symbol has specific schedules — return match or null (outside session)
            return bestMatch;
        }

        // Fall back to global session resolution
        return GetCurrentSession(utcTime);
    }

    public bool IsSessionAllowed(TradingSession session, IReadOnlyList<TradingSession> allowedSessions)
    {
        return allowedSessions.Contains(session);
    }

    public decimal GetSessionQuality(TradingSession session) => session switch
    {
        TradingSession.LondonNYOverlap => 1.0m,
        TradingSession.London          => 0.85m,
        TradingSession.NewYork         => 0.80m,
        TradingSession.Asian           => 0.50m,
        _                              => 0.60m,
    };

    // ── Hardcoded default ranges (fallback) ──────────────────────────────────

    private static TradingSession GetCurrentSessionFromDefaults(DateTime utcTime)
    {
        var time = utcTime.TimeOfDay;

        var londonNYStart = new TimeSpan(13, 0, 0);
        var londonNYEnd   = new TimeSpan(17, 0, 0);
        var londonStart   = new TimeSpan(8,  0, 0);
        var londonEnd     = new TimeSpan(17, 0, 0);
        var newYorkStart  = new TimeSpan(13, 0, 0);
        var newYorkEnd    = new TimeSpan(22, 0, 0);
        var asianStart    = new TimeSpan(0,  0, 0);
        var asianEnd      = new TimeSpan(9,  0, 0);

        // LondonNYOverlap has highest priority
        if (time >= londonNYStart && time < londonNYEnd)
            return TradingSession.LondonNYOverlap;

        if (time >= londonStart && time < londonEnd)
            return TradingSession.London;

        if (time >= newYorkStart && time < newYorkEnd)
            return TradingSession.NewYork;

        if (time >= asianStart && time < asianEnd)
            return TradingSession.Asian;

        // Late evening (22:00–24:00) — treat as Asian pre-open
        return TradingSession.Asian;
    }

    // ── Persisted session data loading ───────────────────────────────────────

    private void RefreshIfStale()
    {
        if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval) return;

        lock (_lock)
        {
            // Double-check inside lock
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval) return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var dbContext = readContext.GetDbContext();

                var configs = dbContext.Set<Domain.Entities.EngineConfig>()
                    .AsNoTracking()
                    .Where(c => c.Key.StartsWith("EA:TradingSessions:"))
                    .Select(c => c.Value)
                    .ToList();

                if (configs.Count > 0)
                {
                    var allSessions = new List<TradingSessionItem>();
                    foreach (var json in configs)
                    {
                        try
                        {
                            var items = JsonSerializer.Deserialize<List<TradingSessionItem>>(json);
                            if (items is not null)
                                allSessions.AddRange(items);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "SessionFilter: failed to parse persisted session JSON");
                        }
                    }

                    _persistedSessions = allSessions.Count > 0 ? allSessions : null;
                }
                else
                {
                    _persistedSessions = null;
                }

                // Also load from dedicated TradingSessionSchedule table (preferred source)
                try
                {
                    var schedules = dbContext.Set<Domain.Entities.TradingSessionSchedule>()
                        .AsNoTracking()
                        .Where(s => !s.IsDeleted)
                        .ToList();

                    // Populate per-symbol session cache
                    _symbolSessions.Clear();
                    foreach (var schedule in schedules)
                    {
                        var entry = new SessionScheduleEntry(
                            schedule.SessionName, schedule.OpenTime, schedule.CloseTime,
                            schedule.DayOfWeekStart, schedule.DayOfWeekEnd);
                        _symbolSessions.AddOrUpdate(
                            schedule.Symbol,
                            _ => new List<SessionScheduleEntry> { entry },
                            (_, list) => { list.Add(entry); return list; });
                    }

                    if (schedules.Count > 0)
                    {
                        // Build a lookup of existing sessions by (Symbol, SessionName) for dedup
                        var existingKeys = new HashSet<string>(
                            (_persistedSessions ?? new List<TradingSessionItem>())
                                .Select(s => $"{s.Symbol}:{s.SessionName}"));

                        var merged = _persistedSessions ?? new List<TradingSessionItem>();

                        foreach (var schedule in schedules)
                        {
                            var key = $"{schedule.Symbol}:{schedule.SessionName}";
                            if (existingKeys.Contains(key))
                            {
                                // Replace the JSON-sourced entry with the entity-sourced one
                                merged.RemoveAll(s => s.Symbol == schedule.Symbol && s.SessionName == schedule.SessionName);
                            }

                            merged.Add(new TradingSessionItem
                            {
                                Symbol = schedule.Symbol,
                                SessionName = schedule.SessionName,
                                OpenTime = schedule.OpenTime,
                                CloseTime = schedule.CloseTime,
                                DayOfWeekStart = schedule.DayOfWeekStart,
                                DayOfWeekEnd = schedule.DayOfWeekEnd,
                            });
                        }

                        _persistedSessions = merged.Count > 0 ? merged : null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SessionFilter: TradingSessionSchedule query failed -- using EngineConfig fallback");
                }

                _lastRefreshUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionFilter: failed to load persisted session data, using defaults");
                _lastRefreshUtc = DateTime.UtcNow; // Don't hammer on repeated failures
            }
        }
    }

    private static TradingSession? ResolveFromPersisted(DateTime utcTime, List<TradingSessionItem> sessions)
    {
        var time = utcTime.TimeOfDay;
        var dayOfWeek = (int)utcTime.DayOfWeek;

        // Priority order: LondonNYOverlap > London > NewYork > Asian
        TradingSession? bestMatch = null;
        int bestPriority = int.MaxValue;

        foreach (var s in sessions)
        {
            if (dayOfWeek < s.DayOfWeekStart || dayOfWeek > s.DayOfWeekEnd)
                continue;

            if (time < s.OpenTime || time >= s.CloseTime)
                continue;

            if (!TryParseSession(s.SessionName, out var session))
                continue;

            var priority = GetSessionPriority(session);
            if (priority < bestPriority)
            {
                bestMatch = session;
                bestPriority = priority;
            }
        }

        return bestMatch;
    }

    private static bool TryParseSession(string name, out TradingSession session)
    {
        return Enum.TryParse(name, ignoreCase: true, out session);
    }

    private static int GetSessionPriority(TradingSession session) => session switch
    {
        TradingSession.LondonNYOverlap => 0,
        TradingSession.London          => 1,
        TradingSession.NewYork         => 2,
        TradingSession.Asian           => 3,
        _                              => 4,
    };

    private record SessionScheduleEntry(
        string SessionName, TimeSpan OpenTime, TimeSpan CloseTime,
        int DayOfWeekStart, int DayOfWeekEnd);

    private sealed class NoOpServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NoOpServiceScope();
    }

    private sealed class NoOpServiceScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new EmptyServiceProvider();

        public void Dispose()
        {
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
