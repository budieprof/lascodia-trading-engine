using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects opposing approved ML signals across configured correlated currency pairs,
/// raises a durable pair-specific alert, and optionally rejects the not-yet-ordered
/// approved signals so the order bridge cannot act on contradictory correlated exposure.
/// </summary>
public sealed class MLCorrelatedSignalConflictWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLCorrelatedSignalConflictWorker);

    private const string CK_PollSecs   = "MLCorrelation:PollIntervalSeconds";
    private const string CK_Window     = "MLCorrelation:WindowMinutes";
    private const string CK_PairMap    = "MLCorrelation:PairMap";
    private const string CK_AlertDest  = "MLCorrelation:AlertDestination";
    private const string CK_RejectSignals = "MLCorrelation:RejectConflictingApprovedSignals";
    private const string DistributedLockKey = "ml:correlated-signal-conflict:cycle";
    private const string AlertDeduplicationPrefix = "ml-correlated-signal-conflict:";
    private const string RejectionReason = "Rejected by MLCorrelatedSignalConflictWorker: correlated approved ML signals conflict.";

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLCorrelatedSignalConflictWorker> _logger;
    private readonly MLCorrelatedSignalConflictOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    internal readonly record struct MLCorrelatedSignalConflictWorkerSettings(
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollJitterSeconds,
        int WindowMinutes,
        string PairMapJson,
        string AlertDestination,
        bool RejectConflictingApprovedSignals,
        int MaxSignalsPerCycle,
        int LockTimeoutSeconds,
        int AlertCooldownSeconds);

    internal readonly record struct MLCorrelatedSignalConflictCycleResult(
        MLCorrelatedSignalConflictWorkerSettings Settings,
        string? SkippedReason,
        int ConfiguredPairCount,
        int CandidateSignalCount,
        int ConflictsDetected,
        int AlertsUpserted,
        int AlertsResolved,
        int SignalsRejected)
    {
        public static MLCorrelatedSignalConflictCycleResult Skipped(
            MLCorrelatedSignalConflictWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0);
    }

    private readonly record struct CorrelatedPair(string LeftSymbol, string RightSymbol)
    {
        public string DeduplicationKey => AlertDeduplicationPrefix + LeftSymbol + ":" + RightSymbol;
    }

    private sealed record SignalSnapshot(
        long Id,
        string Symbol,
        TradeDirection Direction,
        DateTime GeneratedAt,
        decimal? MLConfidenceScore,
        bool HasOrder);

    private sealed record ConflictCandidate(
        CorrelatedPair Pair,
        SignalSnapshot LeftSignal,
        SignalSnapshot RightSignal,
        IReadOnlyCollection<long> ConflictingSignalIds);

    public MLCorrelatedSignalConflictWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLCorrelatedSignalConflictWorker> logger,
        MLCorrelatedSignalConflictOptions? options = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLCorrelatedSignalConflictOptions();
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = BuildSettings(_options);
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Detects opposing approved ML signals across configured correlated pairs.",
            initialSettings.PollInterval);

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName) + initialSettings.InitialDelay;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var delaySettings = BuildSettings(_options);

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                    var result = await RunCycleAsync(stoppingToken);
                    delaySettings = result.Settings;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateSignalCount);
                    _healthMonitor?.RecordCycleSuccess(
                        WorkerName,
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _consecutiveFailures = 0;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _metrics?.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", WorkerName));
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                await Task.Delay(
                    CalculateDelay(GetIntervalWithJitter(delaySettings), _consecutiveFailures),
                    _timeProvider,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopping.", WorkerName);
        }
    }

    internal async Task<MLCorrelatedSignalConflictCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var settings = BuildSettings(_options);
        var started = Stopwatch.GetTimestamp();

        try
        {
            IAsyncDisposable? cycleLock = null;
            if (_distributedLock is null)
            {
                _metrics?.MLCorrelatedSignalConflictLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "unavailable"));

                if (!_missingDistributedLockWarningEmitted)
                {
                    _logger.LogWarning(
                        "{Worker} running without IDistributedLock; duplicate conflict cycles are possible in multi-instance deployments.",
                        WorkerName);
                    _missingDistributedLockWarningEmitted = true;
                }
            }
            else
            {
                cycleLock = await _distributedLock.TryAcquireAsync(
                    DistributedLockKey,
                    TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                    ct);

                if (cycleLock is null)
                {
                    _metrics?.MLCorrelatedSignalConflictLockAttempts.Add(
                        1,
                        new KeyValuePair<string, object?>("outcome", "busy"));
                    RecordCycleSkipped("lock_busy");
                    return MLCorrelatedSignalConflictCycleResult.Skipped(settings, "lock_busy");
                }

                _metrics?.MLCorrelatedSignalConflictLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "acquired"));
            }

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var writeDb = writeContext.GetDbContext();

                    var runtimeSettings = await LoadRuntimeSettingsAsync(writeDb, settings, ct);
                    return await DetectConflictsAsync(writeDb, runtimeSettings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }
        finally
        {
            _metrics?.MLCorrelatedSignalConflictCycleDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        throw new UnreachableException($"{WorkerName} cycle completed without producing a result.");
    }

    private async Task<MLCorrelatedSignalConflictCycleResult> DetectConflictsAsync(
        DbContext db,
        MLCorrelatedSignalConflictWorkerSettings settings,
        CancellationToken ct)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyList<CorrelatedPair> pairs;
        try
        {
            pairs = ParsePairs(settings.PairMapJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Worker}: failed to parse MLCorrelation:PairMap JSON.", WorkerName);
            RecordCycleSkipped("invalid_pair_map");
            return MLCorrelatedSignalConflictCycleResult.Skipped(settings, "invalid_pair_map");
        }

        if (pairs.Count == 0)
        {
            int resolved = await ResolveStaleAlertsAsync(db, new HashSet<string>(), nowUtc, ct);
            await db.SaveChangesAsync(ct);
            _metrics?.MLCorrelatedSignalConflictAlertsResolved.Add(resolved);
            RecordCycleSkipped("empty_pair_map");
            return new MLCorrelatedSignalConflictCycleResult(
                settings,
                "empty_pair_map",
                0,
                0,
                0,
                0,
                resolved,
                0);
        }

        DateTime cutoff = nowUtc.AddMinutes(-settings.WindowMinutes);
        var configuredSymbols = pairs
            .SelectMany(pair => new[] { pair.LeftSymbol, pair.RightSymbol })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var candidateSignals = await db.Set<TradeSignal>()
            .Where(signal => signal.Status == TradeSignalStatus.Approved
                             && signal.GeneratedAt >= cutoff
                             && signal.ExpiresAt > nowUtc
                             && signal.MLPredictedDirection.HasValue
                             && !signal.IsDeleted
                             && configuredSymbols.Contains(signal.Symbol.ToUpper()))
            .OrderByDescending(signal => signal.GeneratedAt)
            .ThenByDescending(signal => signal.Id)
            .Take(settings.MaxSignalsPerCycle)
            .ToListAsync(ct);

        if (candidateSignals.Count == 0)
        {
            int resolved = await ResolveStaleAlertsAsync(db, new HashSet<string>(), nowUtc, ct);
            await db.SaveChangesAsync(ct);
            _metrics?.MLCorrelatedSignalConflictAlertsResolved.Add(resolved);
            RecordCycleSkipped("no_candidate_signals");

            return new MLCorrelatedSignalConflictCycleResult(
                settings,
                "no_candidate_signals",
                pairs.Count,
                0,
                0,
                0,
                resolved,
                0);
        }

        var signalsBySymbol = candidateSignals
            .GroupBy(signal => NormalizeSymbol(signal.Symbol), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(signal => new SignalSnapshot(
                        signal.Id,
                        NormalizeSymbol(signal.Symbol),
                        signal.MLPredictedDirection!.Value,
                        NormalizeUtc(signal.GeneratedAt),
                        signal.MLConfidenceScore,
                        signal.OrderId.HasValue))
                    .OrderByDescending(signal => signal.GeneratedAt)
                    .ThenByDescending(signal => signal.Id)
                    .ToArray(),
                StringComparer.Ordinal);

        var conflicts = new List<ConflictCandidate>();
        foreach (var pair in pairs)
        {
            if (!signalsBySymbol.TryGetValue(pair.LeftSymbol, out var leftSignals)
                || !signalsBySymbol.TryGetValue(pair.RightSymbol, out var rightSignals))
            {
                continue;
            }

            var conflictingIds = new HashSet<long>();
            SignalSnapshot? representativeLeft = null;
            SignalSnapshot? representativeRight = null;

            foreach (var left in leftSignals)
            {
                foreach (var right in rightSignals)
                {
                    if (left.Direction == right.Direction)
                        continue;

                    conflictingIds.Add(left.Id);
                    conflictingIds.Add(right.Id);

                    if (representativeLeft is null
                        || representativeRight is null
                        || IsNewerConflictPair(left, right, representativeLeft, representativeRight))
                    {
                        representativeLeft = left;
                        representativeRight = right;
                    }
                }
            }

            if (representativeLeft is not null && representativeRight is not null)
            {
                conflicts.Add(new ConflictCandidate(
                    pair,
                    representativeLeft,
                    representativeRight,
                    conflictingIds.OrderBy(id => id).ToArray()));
            }
        }

        var activeConflictKeys = conflicts
            .Select(conflict => conflict.Pair.DeduplicationKey)
            .ToHashSet(StringComparer.Ordinal);

        int alertsResolved = await ResolveStaleAlertsAsync(db, activeConflictKeys, nowUtc, ct);
        int alertsUpserted = 0;
        foreach (var conflict in conflicts)
        {
            await UpsertAlertAsync(db, conflict, settings, nowUtc, ct);
            alertsUpserted++;
        }

        int rejected = settings.RejectConflictingApprovedSignals
            ? RejectConflictingSignals(candidateSignals, conflicts)
            : 0;

        await db.SaveChangesAsync(ct);

        _metrics?.MLCorrelatedSignalConflictConflictsDetected.Add(conflicts.Count);
        _metrics?.MLCorrelatedSignalConflictAlertsUpserted.Add(alertsUpserted);
        _metrics?.MLCorrelatedSignalConflictAlertsResolved.Add(alertsResolved);
        _metrics?.MLCorrelatedSignalConflictSignalsRejected.Add(rejected);

        if (conflicts.Count > 0)
        {
            _logger.LogWarning(
                "{Worker}: detected {Count} correlated signal conflict(s), rejected {Rejected} approved signal(s).",
                WorkerName,
                conflicts.Count,
                rejected);
        }

        return new MLCorrelatedSignalConflictCycleResult(
            settings,
            SkippedReason: null,
            ConfiguredPairCount: pairs.Count,
            CandidateSignalCount: candidateSignals.Count,
            ConflictsDetected: conflicts.Count,
            AlertsUpserted: alertsUpserted,
            AlertsResolved: alertsResolved,
            SignalsRejected: rejected);
    }

    private static bool IsNewerConflictPair(
        SignalSnapshot candidateLeft,
        SignalSnapshot candidateRight,
        SignalSnapshot currentLeft,
        SignalSnapshot currentRight)
    {
        var candidateGeneratedAt = candidateLeft.GeneratedAt >= candidateRight.GeneratedAt
            ? candidateLeft.GeneratedAt
            : candidateRight.GeneratedAt;
        var currentGeneratedAt = currentLeft.GeneratedAt >= currentRight.GeneratedAt
            ? currentLeft.GeneratedAt
            : currentRight.GeneratedAt;

        if (candidateGeneratedAt != currentGeneratedAt)
            return candidateGeneratedAt > currentGeneratedAt;

        return Math.Max(candidateLeft.Id, candidateRight.Id) > Math.Max(currentLeft.Id, currentRight.Id);
    }

    private static int RejectConflictingSignals(
        IReadOnlyCollection<TradeSignal> candidateSignals,
        IReadOnlyCollection<ConflictCandidate> conflicts)
    {
        if (conflicts.Count == 0)
            return 0;

        var conflictingIds = conflicts
            .SelectMany(conflict => conflict.ConflictingSignalIds)
            .ToHashSet();
        int rejected = 0;

        foreach (var signal in candidateSignals)
        {
            if (!conflictingIds.Contains(signal.Id)
                || signal.Status != TradeSignalStatus.Approved
                || signal.OrderId.HasValue)
            {
                continue;
            }

            signal.Status = TradeSignalStatus.Rejected;
            signal.RejectionReason = RejectionReason;
            rejected++;
        }

        return rejected;
    }

    private static async Task UpsertAlertAsync(
        DbContext db,
        ConflictCandidate conflict,
        MLCorrelatedSignalConflictWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alerts = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .Where(alert => alert.DeduplicationKey == conflict.Pair.DeduplicationKey)
            .OrderByDescending(alert => alert.Id)
            .ToListAsync(ct);

        var alert = alerts.FirstOrDefault(candidate => !candidate.IsDeleted);
        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = conflict.Pair.DeduplicationKey
            };
            db.Set<Alert>().Add(alert);
        }

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = conflict.Pair.LeftSymbol;
        alert.Severity = AlertSeverity.High;
        alert.CooldownSeconds = settings.AlertCooldownSeconds;
        alert.ConditionJson = BuildAlertConditionJson(conflict, settings);
        alert.IsActive = true;
        alert.AutoResolvedAt = null;
        alert.IsDeleted = false;

        foreach (var duplicate in alerts.Where(candidate => candidate.Id != alert.Id && !candidate.IsDeleted))
        {
            duplicate.IsActive = false;
            duplicate.AutoResolvedAt ??= nowUtc;
        }
    }

    private static async Task<int> ResolveStaleAlertsAsync(
        DbContext db,
        IReadOnlySet<string> activeConflictKeys,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeAlerts = await db.Set<Alert>()
            .Where(alert => alert.AlertType == AlertType.MLModelDegraded
                            && alert.DeduplicationKey != null
                            && alert.DeduplicationKey.StartsWith(AlertDeduplicationPrefix)
                            && alert.IsActive)
            .ToListAsync(ct);

        int resolved = 0;
        foreach (var alert in activeAlerts)
        {
            if (alert.DeduplicationKey is not null && activeConflictKeys.Contains(alert.DeduplicationKey))
                continue;

            alert.IsActive = false;
            alert.AutoResolvedAt = nowUtc;
            resolved++;
        }

        return resolved;
    }

    private static IReadOnlyList<CorrelatedPair> ParsePairs(string pairMapJson)
    {
        var rawMap = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            string.IsNullOrWhiteSpace(pairMapJson) ? "{}" : pairMapJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        var pairsByKey = new Dictionary<string, CorrelatedPair>(StringComparer.Ordinal);
        foreach (var (baseSymbolRaw, peers) in rawMap)
        {
            var baseSymbol = NormalizeSymbol(baseSymbolRaw);
            if (baseSymbol.Length == 0 || peers is null)
                continue;

            foreach (var peerRaw in peers)
            {
                var peer = NormalizeSymbol(peerRaw);
                if (peer.Length == 0 || string.Equals(baseSymbol, peer, StringComparison.Ordinal))
                    continue;

                var ordered = string.CompareOrdinal(baseSymbol, peer) <= 0
                    ? (Left: baseSymbol, Right: peer)
                    : (Left: peer, Right: baseSymbol);
                pairsByKey[ordered.Left + ":" + ordered.Right] = new CorrelatedPair(ordered.Left, ordered.Right);
            }
        }

        return pairsByKey.Values
            .OrderBy(pair => pair.LeftSymbol, StringComparer.Ordinal)
            .ThenBy(pair => pair.RightSymbol, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildAlertConditionJson(
        ConflictCandidate conflict,
        MLCorrelatedSignalConflictWorkerSettings settings)
        => JsonSerializer.Serialize(new
        {
            reason = "correlated_signal_conflict",
            severity = "high",
            destination = settings.AlertDestination,
            leftSymbol = conflict.Pair.LeftSymbol,
            rightSymbol = conflict.Pair.RightSymbol,
            leftSignalId = conflict.LeftSignal.Id,
            rightSignalId = conflict.RightSignal.Id,
            leftDirection = conflict.LeftSignal.Direction.ToString(),
            rightDirection = conflict.RightSignal.Direction.ToString(),
            leftGeneratedAt = conflict.LeftSignal.GeneratedAt,
            rightGeneratedAt = conflict.RightSignal.GeneratedAt,
            leftMlConfidence = conflict.LeftSignal.MLConfidenceScore,
            rightMlConfidence = conflict.RightSignal.MLConfidenceScore,
            leftOrderLinked = conflict.LeftSignal.HasOrder,
            rightOrderLinked = conflict.RightSignal.HasOrder,
            conflictingSignalCount = conflict.ConflictingSignalIds.Count,
            conflictingSignalIds = conflict.ConflictingSignalIds.OrderBy(id => id).Take(20),
            windowMinutes = settings.WindowMinutes,
            rejectedApprovedSignals = settings.RejectConflictingApprovedSignals
        });

    private async Task<MLCorrelatedSignalConflictWorkerSettings> LoadRuntimeSettingsAsync(
        DbContext db,
        MLCorrelatedSignalConflictWorkerSettings defaults,
        CancellationToken ct)
        => defaults with
        {
            PollInterval = TimeSpan.FromSeconds(ClampInt(
                await GetConfigAsync(db, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds, ct),
                300,
                30,
                24 * 60 * 60)),
            WindowMinutes = ClampInt(
                await GetConfigAsync(db, CK_Window, defaults.WindowMinutes, ct),
                defaults.WindowMinutes,
                1,
                24 * 60),
            PairMapJson = await GetConfigAsync(db, CK_PairMap, defaults.PairMapJson, ct),
            AlertDestination = NormalizeDestination(
                await GetConfigAsync(db, CK_AlertDest, defaults.AlertDestination, ct)),
            RejectConflictingApprovedSignals = await GetConfigAsync(
                db,
                CK_RejectSignals,
                defaults.RejectConflictingApprovedSignals,
                ct)
        };

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLCorrelatedSignalConflictCyclesSkipped.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private static MLCorrelatedSignalConflictWorkerSettings BuildSettings(
        MLCorrelatedSignalConflictOptions options)
        => new(
            InitialDelay: TimeSpan.FromSeconds(ClampInt(options.InitialDelaySeconds, 60, 0, 24 * 60 * 60)),
            PollInterval: TimeSpan.FromSeconds(ClampInt(options.PollIntervalSeconds, 300, 30, 24 * 60 * 60)),
            PollJitterSeconds: ClampInt(options.PollJitterSeconds, 30, 0, 24 * 60 * 60),
            WindowMinutes: ClampInt(options.WindowMinutes, 60, 1, 24 * 60),
            PairMapJson: string.IsNullOrWhiteSpace(options.PairMapJson) ? "{}" : options.PairMapJson,
            AlertDestination: NormalizeDestination(options.AlertDestination),
            RejectConflictingApprovedSignals: options.RejectConflictingApprovedSignals,
            MaxSignalsPerCycle: ClampInt(options.MaxSignalsPerCycle, 1_000, 1, 10_000),
            LockTimeoutSeconds: ClampInt(options.LockTimeoutSeconds, 5, 0, 300),
            AlertCooldownSeconds: ClampInt(options.AlertCooldownSeconds, 1_800, 60, 24 * 60 * 60));

    private static TimeSpan GetIntervalWithJitter(MLCorrelatedSignalConflictWorkerSettings settings)
        => settings.PollJitterSeconds == 0
            ? settings.PollInterval
            : settings.PollInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, settings.PollJitterSeconds + 1));

    private static int ClampInt(int value, int defaultValue, int min, int max)
        => value < min || value > max ? Math.Clamp(defaultValue, min, max) : value;

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();

    private static string NormalizeDestination(string? value)
        => string.IsNullOrWhiteSpace(value) ? "ml-ops" : value.Trim();

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.Key == key && !config.IsDeleted, ct);

        if (entry?.Value is null)
            return defaultValue;

        if (typeof(T) == typeof(string))
            return (T)(object)entry.Value;

        if (typeof(T) == typeof(bool))
        {
            if (bool.TryParse(entry.Value, out var boolValue))
                return (T)(object)boolValue;

            if (entry.Value == "1")
                return (T)(object)true;

            if (entry.Value == "0")
                return (T)(object)false;
        }

        if (typeof(T) == typeof(int)
            && int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            return (T)(object)intValue;

        return defaultValue;
    }
}
