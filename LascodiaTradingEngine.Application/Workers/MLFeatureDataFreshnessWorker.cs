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
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects stale ML feature data sources and publishes both machine-readable stale flags
/// and operator alerts for COT, sentiment, and per-symbol/timeframe candle inputs.
/// </summary>
public sealed class MLFeatureDataFreshnessWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLFeatureDataFreshnessWorker);

    private const string DistributedLockKey = "workers:ml-feature-data-freshness:cycle";
    private const string ConfigPrefix = "MLFeatureStale:";
    private const string CandleSourcePrefix = "Candle:";

    private const string CK_Enabled = "MLFeatureStale:Enabled";
    private const string CK_InitialDelaySeconds = "MLFeatureStale:InitialDelaySeconds";
    private const string CK_PollSecs = "MLFeatureStale:PollIntervalSeconds";
    private const string CK_MaxCotAgeDays = "MLFeatureStale:MaxCotAgeDays";
    private const string CK_MaxSentimentAgeHrs = "MLFeatureStale:MaxSentimentAgeHours";
    private const string CK_CandleStaleMult = "MLFeatureStale:CandleStaleMultiplier";
    private const string CK_MaxPairsPerCycle = "MLFeatureStale:MaxPairsPerCycle";
    private const string CK_LockTimeoutSeconds = "MLFeatureStale:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLFeatureStale:DbCommandTimeoutSeconds";
    private const string CK_AlertDestination = "MLFeatureStale:AlertDestination";

    private const int DefaultPollSeconds = 1800;
    private const int DefaultMaxCotAgeDays = 10;
    private const int DefaultMaxSentimentAgeHours = 24;
    private const int DefaultMaxPairsPerCycle = 5000;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultDbCommandTimeoutSeconds = 30;
    private const double DefaultCandleStaleMultiplier = 3.0;
    private const string DefaultAlertDestination = "ml-ops";

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySeconds,
        CK_PollSecs,
        CK_MaxCotAgeDays,
        CK_MaxSentimentAgeHrs,
        CK_CandleStaleMult,
        CK_MaxPairsPerCycle,
        CK_LockTimeoutSeconds,
        CK_DbCommandTimeoutSeconds,
        CK_AlertDestination,
        AlertCooldownDefaults.CK_MLMonitoring
    ];

    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureDataFreshnessWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLFeatureDataFreshnessOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLFeatureDataFreshnessWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureDataFreshnessWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLFeatureDataFreshnessOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLFeatureDataFreshnessOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Monitors ML feature data freshness for COT, sentiment, and candle inputs.",
            TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds)));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds));

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                               + TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(_options.InitialDelaySeconds));
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                    _metrics?.MLFeatureDataFreshnessTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var started = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Config.PollInterval;

                        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.StaleSourceCount);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            elapsedMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLFeatureDataFreshnessCycleDurationMs.Record(elapsedMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug(
                                "{Worker}: cycle skipped ({Reason}).",
                                WorkerName,
                                result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: checked={Checked}, stale={Stale}, alertsUpserted={AlertsUpserted}, alertsResolved={AlertsResolved}.",
                                WorkerName,
                                result.SourceCount,
                                result.StaleSourceCount,
                                result.AlertsUpserted,
                                result.AlertsResolved);
                        }

                        var previousFailures = ConsecutiveCycleFailures;
                        if (previousFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, previousFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName,
                                previousFailures);
                        }

                        ConsecutiveCycleFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveCycleFailuresField);
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_feature_data_freshness_cycle"));
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                var delay = ConsecutiveCycleFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveCycleFailures)
                    : WakeInterval;
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<FeatureDataFreshnessCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            return FeatureDataFreshnessCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLFeatureDataFreshnessLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate freshness cycles are possible in multi-instance deployments.",
                    WorkerName);
            }
        }
        else
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLFeatureDataFreshnessLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return FeatureDataFreshnessCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLFeatureDataFreshnessLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunFreshnessAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task<FeatureDataFreshnessCycleResult> RunFreshnessAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
            return FeatureDataFreshnessCycleResult.Skipped(config, "disabled");

        return await RunFreshnessAsync(readCtx, writeCtx, config, ct);
    }

    private async Task<FeatureDataFreshnessCycleResult> RunFreshnessAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureDataFreshnessConfig config,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var configSpecs = new List<EngineConfigUpsertSpec>();

        var results = new List<FreshnessSourceResult>
        {
            await CheckCotFreshnessAsync(readCtx, writeCtx, config, nowUtc, configSpecs, ct),
            await CheckSentimentFreshnessAsync(readCtx, writeCtx, config, nowUtc, configSpecs, ct),
        };

        var candleResults = await CheckCandleFreshnessAsync(readCtx, writeCtx, config, nowUtc, configSpecs, ct);
        results.AddRange(candleResults);

        var stalePairAlertsResolved = await ResolveStaleCandleAlertsAsync(
            writeCtx,
            candleResults.Select(result => result.DeduplicationKey).ToHashSet(StringComparer.Ordinal),
            nowUtc,
            ct);

        if (configSpecs.Count > 0)
            await EngineConfigUpsert.BatchUpsertAsync(writeCtx, configSpecs, ct);

        await writeCtx.SaveChangesAsync(ct);

        int sourceCount = results.Count;
        int staleCount = results.Count(result => result.IsStale);
        int alertsUpserted = results.Count(result => result.AlertUpserted);
        int alertsResolved = results.Count(result => result.AlertResolved) + stalePairAlertsResolved;

        _metrics?.MLFeatureDataFreshnessSourcesChecked.Add(sourceCount);
        if (staleCount > 0)
            _metrics?.MLFeatureDataFreshnessSourcesStale.Add(staleCount);
        _metrics?.MLFeatureDataFreshnessActiveCandlePairs.Record(candleResults.Count);
        RecordAlertTransitions(alertsUpserted, alertsResolved);

        return new FeatureDataFreshnessCycleResult(
            config,
            SourceCount: sourceCount,
            StaleSourceCount: staleCount,
            AlertsUpserted: alertsUpserted,
            AlertsResolved: alertsResolved,
            ActiveCandlePairCount: candleResults.Count,
            ConfigRowsWritten: configSpecs.Count,
            SkippedReason: null);
    }

    private async Task<FreshnessSourceResult> CheckCotFreshnessAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureDataFreshnessConfig config,
        DateTime nowUtc,
        List<EngineConfigUpsertSpec> configSpecs,
        CancellationToken ct)
    {
        var latest = await readCtx.Set<COTReport>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted)
            .OrderByDescending(c => c.ReportDate)
            .Select(c => (DateTime?)c.ReportDate)
            .FirstOrDefaultAsync(ct);

        var maxAge = TimeSpan.FromDays(config.MaxCotAgeDays);
        var result = EvaluateSource(
            SourceName: "COT",
            DeduplicationKey: "MLFeatureStale:COT",
            ConfigSourceKey: "COT",
            Symbol: null,
            LastSeenAt: latest,
            MaxAge: maxAge,
            NowUtc: nowUtc);

        AddSourceConfig(configSpecs, result, nowUtc, $"Max age {config.MaxCotAgeDays} day(s).");

        if (result.IsStale)
        {
            await UpsertAlertAsync(
                writeCtx,
                result,
                config,
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    source = "COT",
                    lastSeenAt = result.LastSeenValue,
                    maxAgeDays = config.MaxCotAgeDays,
                    ageSeconds = result.Age?.TotalSeconds,
                    alertDestination = config.AlertDestination,
                }),
                ct);

            _logger.LogWarning(
                "COT data is stale (last seen {LastSeen}, max age {MaxDays}d).",
                result.LastSeenValue,
                config.MaxCotAgeDays);

            return result with { AlertUpserted = true };
        }

        var resolved = await ResolveAlertAsync(writeCtx, result.DeduplicationKey, nowUtc, ct);
        return result with { AlertResolved = resolved };
    }

    private async Task<FreshnessSourceResult> CheckSentimentFreshnessAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureDataFreshnessConfig config,
        DateTime nowUtc,
        List<EngineConfigUpsertSpec> configSpecs,
        CancellationToken ct)
    {
        var latest = await readCtx.Set<SentimentSnapshot>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted)
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => (DateTime?)s.CapturedAt)
            .FirstOrDefaultAsync(ct);

        var maxAge = TimeSpan.FromHours(config.MaxSentimentAgeHours);
        var result = EvaluateSource(
            SourceName: "Sentiment",
            DeduplicationKey: "MLFeatureStale:Sentiment",
            ConfigSourceKey: "Sentiment",
            Symbol: null,
            LastSeenAt: latest,
            MaxAge: maxAge,
            NowUtc: nowUtc);

        AddSourceConfig(configSpecs, result, nowUtc, $"Max age {config.MaxSentimentAgeHours} hour(s).");

        if (result.IsStale)
        {
            await UpsertAlertAsync(
                writeCtx,
                result,
                config,
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    source = "Sentiment",
                    lastSeenAt = result.LastSeenValue,
                    maxAgeHours = config.MaxSentimentAgeHours,
                    ageSeconds = result.Age?.TotalSeconds,
                    alertDestination = config.AlertDestination,
                }),
                ct);

            _logger.LogWarning(
                "Sentiment data is stale (last seen {LastSeen}, max age {MaxHours}h).",
                result.LastSeenValue,
                config.MaxSentimentAgeHours);

            return result with { AlertUpserted = true };
        }

        var resolved = await ResolveAlertAsync(writeCtx, result.DeduplicationKey, nowUtc, ct);
        return result with { AlertResolved = resolved };
    }

    private async Task<IReadOnlyList<FreshnessSourceResult>> CheckCandleFreshnessAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureDataFreshnessConfig config,
        DateTime nowUtc,
        List<EngineConfigUpsertSpec> configSpecs,
        CancellationToken ct)
    {
        var pairQuery = readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct();

        var pairs = await pairQuery
            .OrderBy(p => p.Symbol)
            .ThenBy(p => p.Timeframe)
            .Take(config.MaxPairsPerCycle + 1)
            .ToListAsync(ct);

        if (pairs.Count > config.MaxPairsPerCycle)
        {
            pairs.RemoveAt(pairs.Count - 1);
            RecordCycleSkipped("pair_limit");
        }

        if (pairs.Count == 0)
            return [];

        var symbols = pairs.Select(pair => pair.Symbol).Distinct().ToArray();
        var timeframes = pairs.Select(pair => pair.Timeframe).Distinct().ToArray();

        var latestCandles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.IsClosed
                        && !c.IsDeleted
                        && symbols.Contains(c.Symbol)
                        && timeframes.Contains(c.Timeframe))
            .GroupBy(c => new { c.Symbol, c.Timeframe })
            .Select(g => new
            {
                g.Key.Symbol,
                g.Key.Timeframe,
                LastSeenAt = (DateTime?)g.Max(c => c.Timestamp),
            })
            .ToListAsync(ct);

        var latestByPair = latestCandles.ToDictionary(
            row => (row.Symbol, row.Timeframe),
            row => row.LastSeenAt);

        var results = new List<FreshnessSourceResult>(pairs.Count);
        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();

            latestByPair.TryGetValue((pair.Symbol, pair.Timeframe), out var lastSeenAt);
            var thresholdMinutes = TimeframeDurationHelper.BarMinutes(pair.Timeframe) * config.CandleStaleMultiplier;
            var result = EvaluateSource(
                SourceName: "Candle",
                DeduplicationKey: BuildCandleDeduplicationKey(pair.Symbol, pair.Timeframe),
                ConfigSourceKey: $"{CandleSourcePrefix}{pair.Symbol}:{pair.Timeframe}",
                Symbol: pair.Symbol,
                LastSeenAt: lastSeenAt,
                MaxAge: TimeSpan.FromMinutes(thresholdMinutes),
                NowUtc: nowUtc);

            AddSourceConfig(configSpecs, result, nowUtc, $"Max age {thresholdMinutes:F2} minute(s).");

            if (result.IsStale)
            {
                await UpsertAlertAsync(
                    writeCtx,
                    result,
                    config,
                    nowUtc,
                    JsonSerializer.Serialize(new
                    {
                        source = "Candle",
                        symbol = pair.Symbol,
                        timeframe = pair.Timeframe.ToString(),
                        lastSeenAt = result.LastSeenValue,
                        thresholdMinutes,
                        ageSeconds = result.Age?.TotalSeconds,
                        alertDestination = config.AlertDestination,
                    }),
                    ct);

                _logger.LogWarning(
                    "Candle data stale for {Symbol}/{Timeframe} (last seen {LastSeen}, threshold {ThresholdMinutes:F0}min).",
                    pair.Symbol,
                    pair.Timeframe,
                    result.LastSeenValue,
                    thresholdMinutes);

                results.Add(result with { AlertUpserted = true });
            }
            else
            {
                var resolved = await ResolveAlertAsync(writeCtx, result.DeduplicationKey, nowUtc, ct);
                results.Add(result with { AlertResolved = resolved });
            }
        }

        return results;
    }

    private static FreshnessSourceResult EvaluateSource(
        string SourceName,
        string DeduplicationKey,
        string ConfigSourceKey,
        string? Symbol,
        DateTime? LastSeenAt,
        TimeSpan MaxAge,
        DateTime NowUtc)
    {
        var age = LastSeenAt.HasValue ? NowUtc - LastSeenAt.Value : (TimeSpan?)null;
        var isStale = LastSeenAt is null || age > MaxAge;

        return new FreshnessSourceResult(
            SourceName,
            DeduplicationKey,
            ConfigSourceKey,
            Symbol,
            LastSeenAt,
            LastSeenAt?.ToString("O", CultureInfo.InvariantCulture) ?? "never",
            age,
            MaxAge,
            isStale,
            AlertUpserted: false,
            AlertResolved: false);
    }

    private static void AddSourceConfig(
        List<EngineConfigUpsertSpec> specs,
        FreshnessSourceResult result,
        DateTime checkedAtUtc,
        string thresholdDescription)
    {
        var prefix = $"{ConfigPrefix}{result.ConfigSourceKey}";
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:IsStale",
            result.IsStale.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            ConfigDataType.Bool,
            $"Whether {result.SourceName} feature data is currently stale. {thresholdDescription}"));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:LastSeenAt",
            result.LastSeenValue,
            ConfigDataType.String,
            $"Most recent UTC timestamp observed for {result.SourceName} feature data."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:CheckedAt",
            checkedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ConfigDataType.String,
            $"UTC timestamp when {result.SourceName} freshness was last evaluated."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:MaxAgeSeconds",
            Math.Round(result.MaxAge.TotalSeconds, 3).ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Decimal,
            $"Freshness threshold in seconds for {result.SourceName} feature data."));
    }

    private async Task UpsertAlertAsync(
        DbContext writeCtx,
        FreshnessSourceResult result,
        FeatureDataFreshnessConfig config,
        DateTime nowUtc,
        string conditionJson,
        CancellationToken ct)
    {
        var alert = await writeCtx.Set<Alert>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == result.DeduplicationKey && !a.IsDeleted, ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                DeduplicationKey = result.DeduplicationKey,
            };
            writeCtx.Set<Alert>().Add(alert);
        }

        alert.AlertType = AlertType.DataQualityIssue;
        alert.Symbol = result.Symbol;
        alert.ConditionJson = conditionJson;
        alert.Severity = result.LastSeenAt is null ? AlertSeverity.Critical : AlertSeverity.Medium;
        alert.CooldownSeconds = config.AlertCooldownSeconds;
        alert.IsActive = true;
        alert.IsDeleted = false;
        alert.AutoResolvedAt = null;

        await Task.CompletedTask;
    }

    private static async Task<bool> ResolveAlertAsync(
        DbContext writeCtx,
        string deduplicationKey,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await writeCtx.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == deduplicationKey
                                      && a.IsActive
                                      && !a.IsDeleted, ct);

        if (alert is null)
            return false;

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        return true;
    }

    private async Task<int> ResolveStaleCandleAlertsAsync(
        DbContext writeCtx,
        IReadOnlySet<string> activeCandleKeys,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeAlerts = await writeCtx.Set<Alert>()
            .Where(a => a.IsActive
                        && !a.IsDeleted
                        && a.DeduplicationKey != null
                        && a.DeduplicationKey.StartsWith($"{ConfigPrefix}{CandleSourcePrefix}"))
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in activeAlerts)
        {
            if (alert.DeduplicationKey is null || activeCandleKeys.Contains(alert.DeduplicationKey))
                continue;

            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
            resolved++;
        }

        return resolved;
    }

    internal static async Task<FeatureDataFreshnessConfig> LoadConfigAsync(
        DbContext ctx,
        MLFeatureDataFreshnessOptions options,
        CancellationToken ct)
    {
        var rows = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => ConfigKeys.Contains(c.Key) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Key, c.Value, c.LastUpdatedAt })
            .ToListAsync(ct);

        var values = rows
            .Where(c => c.Value is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.LastUpdatedAt).ThenBy(c => c.Id).Last().Value!,
                StringComparer.Ordinal);

        var pollSeconds = NormalizePollSeconds(GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));

        return new FeatureDataFreshnessConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySeconds, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollSeconds: pollSeconds,
            MaxCotAgeDays: NormalizeMaxCotAgeDays(GetConfig(values, CK_MaxCotAgeDays, options.MaxCotAgeDays)),
            MaxSentimentAgeHours: NormalizeMaxSentimentAgeHours(
                GetConfig(values, CK_MaxSentimentAgeHrs, options.MaxSentimentAgeHours)),
            CandleStaleMultiplier: NormalizeCandleStaleMultiplier(
                GetConfig(values, CK_CandleStaleMult, options.CandleStaleMultiplier)),
            MaxPairsPerCycle: NormalizeMaxPairsPerCycle(GetConfig(values, CK_MaxPairsPerCycle, options.MaxPairsPerCycle)),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSeconds, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSeconds, options.DbCommandTimeoutSeconds)),
            AlertDestination: NormalizeDestination(GetConfig(values, CK_AlertDestination, options.AlertDestination)),
            AlertCooldownSeconds: NormalizeAlertCooldownSeconds(
                GetConfig(values, AlertCooldownDefaults.CK_MLMonitoring, options.AlertCooldownSeconds)));
    }

    private static T GetConfig<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
            return defaultValue;

        return TryConvertConfig(raw, out T parsed)
            ? parsed
            : defaultValue;
    }

    private static bool TryConvertConfig<T>(string value, out T result)
    {
        object? parsed = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        var normalized = value.Trim();

        if (targetType == typeof(string))
        {
            parsed = value;
        }
        else if (targetType == typeof(int)
                 && int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
        }
        else if (targetType == typeof(double)
                 && double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            parsed = doubleValue;
        }
        else if (targetType == typeof(bool)
                 && TryParseBool(normalized, out var boolValue))
        {
            parsed = boolValue;
        }

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }

    internal static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : 0;

    internal static int NormalizePollSeconds(int value)
        => value is >= 60 and <= 86_400 ? value : DefaultPollSeconds;

    internal static int NormalizeMaxCotAgeDays(int value)
        => value is >= 1 and <= 60 ? value : DefaultMaxCotAgeDays;

    internal static int NormalizeMaxSentimentAgeHours(int value)
        => value is >= 1 and <= 168 ? value : DefaultMaxSentimentAgeHours;

    internal static double NormalizeCandleStaleMultiplier(double value)
        => double.IsFinite(value) && value is >= 1.0 and <= 100.0 ? value : DefaultCandleStaleMultiplier;

    internal static int NormalizeMaxPairsPerCycle(int value)
        => value is >= 1 and <= 100_000 ? value : DefaultMaxPairsPerCycle;

    internal static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : DefaultLockTimeoutSeconds;

    internal static int NormalizeDbCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : DefaultDbCommandTimeoutSeconds;

    internal static int NormalizeAlertCooldownSeconds(int value)
        => value is >= 1 and <= 604_800 ? value : AlertCooldownDefaults.Default_MLMonitoring;

    internal static string NormalizeDestination(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return DefaultAlertDestination;

        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

    private static bool TryParseBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        return false;
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLFeatureDataFreshnessCyclesSkipped.Add(1, Tag("reason", reason));

    private void RecordAlertTransitions(int upserted, int resolved)
    {
        if (upserted > 0)
            _metrics?.MLFeatureDataFreshnessAlertTransitions.Add(upserted, Tag("transition", "upserted_cycle_total"));
        if (resolved > 0)
            _metrics?.MLFeatureDataFreshnessAlertTransitions.Add(resolved, Tag("transition", "resolved"));
    }

    private static string BuildCandleDeduplicationKey(string symbol, Timeframe timeframe)
        => $"{ConfigPrefix}{CandleSourcePrefix}{symbol}:{timeframe}";

    private static KeyValuePair<string, object?> Tag(string key, object? value)
        => new(key, value);

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException)
        {
            // Some providers do not expose relational command timeout configuration.
        }
    }

    internal sealed record FeatureDataFreshnessConfig(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollSeconds,
        int MaxCotAgeDays,
        int MaxSentimentAgeHours,
        double CandleStaleMultiplier,
        int MaxPairsPerCycle,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds,
        string AlertDestination,
        int AlertCooldownSeconds);

    internal sealed record FeatureDataFreshnessCycleResult(
        FeatureDataFreshnessConfig Config,
        int SourceCount,
        int StaleSourceCount,
        int AlertsUpserted,
        int AlertsResolved,
        int ActiveCandlePairCount,
        int ConfigRowsWritten,
        string? SkippedReason)
    {
        public static FeatureDataFreshnessCycleResult Skipped(FeatureDataFreshnessConfig config, string reason)
            => new(config, 0, 0, 0, 0, 0, 0, reason);
    }

    private sealed record FreshnessSourceResult(
        string SourceName,
        string DeduplicationKey,
        string ConfigSourceKey,
        string? Symbol,
        DateTime? LastSeenAt,
        string LastSeenValue,
        TimeSpan? Age,
        TimeSpan MaxAge,
        bool IsStale,
        bool AlertUpserted,
        bool AlertResolved);
}
