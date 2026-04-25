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
/// Guards the market-data inputs used by active ML models.
/// </summary>
public sealed class MLDataQualityWorker : BackgroundService
{
    private const string WorkerName = nameof(MLDataQualityWorker);
    private const string DistributedLockKey = "ml:data-quality:cycle";
    private const string AlertDedupPrefix = "ml-data-quality:";
    private const string SymbolScope = "symbol";
    private const int AlertConditionMaxLength = 1_000;
    private const double StdEpsilon = 1e-12;

    private const string CK_Enabled = "MLDataQuality:Enabled";
    private const string CK_PollSecs = "MLDataQuality:PollIntervalSeconds";
    private const string CK_GapMult = "MLDataQuality:GapMultiplier";
    private const string CK_SpikeSigmas = "MLDataQuality:SpikeSigmas";
    private const string CK_SpikeBars = "MLDataQuality:SpikeLookbackBars";
    private const string CK_MinSpikeBaselineBars = "MLDataQuality:MinSpikeBaselineBars";
    private const string CK_LiveStale = "MLDataQuality:LivePriceStalenessSeconds";
    private const string CK_FutureTimestampTolerance = "MLDataQuality:FutureTimestampToleranceSeconds";
    private const string CK_MaxPairs = "MLDataQuality:MaxPairsPerCycle";
    private const string CK_LockTimeout = "MLDataQuality:LockTimeoutSeconds";
    private const string CK_AlertCooldown = "MLDataQuality:AlertCooldownSeconds";
    private const string CK_AlertDest = "MLDataQuality:AlertDestination";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLDataQualityWorker> _logger;
    private readonly MLDataQualityOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;
    private bool _missingAlertDispatcherWarningEmitted;

    /// <summary>Initializes the worker.</summary>
    public MLDataQualityWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLDataQualityWorker> logger,
        MLDataQualityOptions? options = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLDataQualityOptions();
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    /// <summary>
    /// Main background loop. Data quality runs more frequently than most ML monitors
    /// because stale candles or live prices can corrupt predictions within one bar.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = BuildSettings(_options);
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Detects missing candles, anomalous closes, and stale live prices for active ML model feeds.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.IssuesDetected);
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
                    _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName));
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

    internal async Task<MLDataQualityCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = BuildSettings(_options);

        try
        {
            if (!settings.Enabled)
            {
                RecordCycleSkipped("disabled");
                return MLDataQualityCycleResult.Skipped(settings, "disabled");
            }

            IAsyncDisposable? cycleLock = null;
            if (_distributedLock is null)
            {
                _metrics?.MLDataQualityLockAttempts.Add(1, Tag("outcome", "unavailable"));
                if (!_missingDistributedLockWarningEmitted)
                {
                    _logger.LogWarning(
                        "{Worker} running without IDistributedLock; duplicate alerts are possible in multi-instance deployments.",
                        WorkerName);
                    _missingDistributedLockWarningEmitted = true;
                }
            }
            else
            {
                cycleLock = await _distributedLock.TryAcquireAsync(
                    DistributedLockKey,
                    settings.LockTimeout,
                    ct);

                if (cycleLock is null)
                {
                    _metrics?.MLDataQualityLockAttempts.Add(1, Tag("outcome", "busy"));
                    RecordCycleSkipped("lock_busy");
                    return MLDataQualityCycleResult.Skipped(settings, "lock_busy");
                }

                _metrics?.MLDataQualityLockAttempts.Add(1, Tag("outcome", "acquired"));
            }

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = writeContext.GetDbContext();
                    var dispatcher = scope.ServiceProvider.GetService<IAlertDispatcher>();

                    if (dispatcher is null && !_missingAlertDispatcherWarningEmitted)
                    {
                        _logger.LogWarning(
                            "{Worker} could not resolve IAlertDispatcher; DataQualityIssue alerts will be persisted but not notified.",
                            WorkerName);
                        _missingAlertDispatcherWarningEmitted = true;
                    }

                    var runtimeSettings = await LoadRuntimeSettingsAsync(db, settings, ct);
                    if (!runtimeSettings.Enabled)
                    {
                        RecordCycleSkipped("disabled");
                        return MLDataQualityCycleResult.Skipped(runtimeSettings, "disabled");
                    }

                    return await CheckAllFeedsAsync(writeContext, db, dispatcher, runtimeSettings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }
        finally
        {
            _metrics?.MLDataQualityCycleDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<MLDataQualityCycleResult> CheckAllFeedsAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDataQualityWorkerSettings settings,
        CancellationToken ct)
    {
        var loadedPairs = await LoadActivePairsAsync(db, settings, ct);
        var pairs = loadedPairs.Pairs;
        var pairsSkipped = loadedPairs.InvalidPairsSkipped;
        var issuesDetected = 0;
        var alertsDispatched = 0;

        if (loadedPairs.Truncated)
            RecordPairSkipped("max_pairs_truncated");

        for (var i = 0; i < loadedPairs.InvalidPairsSkipped; i++)
            RecordPairSkipped("invalid_symbol");

        var activeIssueKeys = new HashSet<string>(StringComparer.Ordinal);
        var evaluatedPairKeys = new HashSet<string>(StringComparer.Ordinal);
        var evaluatedSymbols = new HashSet<string>(StringComparer.Ordinal);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        if (pairs.Count == 0)
        {
            var resolved = await ResolveInactiveAlertsAsync(
                writeContext,
                db,
                dispatcher,
                settings,
                activeIssueKeys,
                evaluatedPairKeys,
                evaluatedSymbols,
                resolveAll: true,
                ct);

            RecordCycleSkipped("no_active_pairs");
            return new MLDataQualityCycleResult(
                settings,
                "no_active_pairs",
                0,
                pairsSkipped,
                0,
                0,
                resolved,
                loadedPairs.Truncated);
        }

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();

            PairEvaluation evaluation;
            try
            {
                evaluation = await EvaluateCandleFeedAsync(db, pair, nowUtc, settings, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                pairsSkipped++;
                RecordPairSkipped("pair_error");
                _logger.LogWarning(
                    ex,
                    "{Worker}: candle-feed quality check failed for {Symbol}/{Timeframe}.",
                    WorkerName,
                    pair.Symbol,
                    pair.Timeframe);
                continue;
            }

            if (!evaluation.Evaluated)
            {
                pairsSkipped++;
                RecordPairSkipped(evaluation.SkipReason ?? "unknown");
                continue;
            }

            evaluatedPairKeys.Add(PairKey(pair.Symbol, pair.Timeframe));
            evaluatedSymbols.Add(pair.Symbol);
            _metrics?.MLDataQualityPairsEvaluated.Add(
                1,
                Tag("symbol", pair.Symbol),
                Tag("timeframe", pair.Timeframe));

            foreach (var issue in evaluation.Issues)
            {
                issuesDetected++;
                activeIssueKeys.Add(issue.DeduplicationKey);
                RecordIssueDetected(issue);

                if (await UpsertAndDispatchAlertAsync(writeContext, db, dispatcher, issue, settings, nowUtc, ct))
                    alertsDispatched++;
            }
        }

        foreach (var symbol in pairs.Select(pair => pair.Symbol).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            evaluatedSymbols.Add(symbol);

            var liveIssues = await EvaluateLivePriceAsync(db, symbol, nowUtc, settings, ct);
            foreach (var issue in liveIssues)
            {
                issuesDetected++;
                activeIssueKeys.Add(issue.DeduplicationKey);
                RecordIssueDetected(issue);

                if (await UpsertAndDispatchAlertAsync(writeContext, db, dispatcher, issue, settings, nowUtc, ct))
                    alertsDispatched++;
            }
        }

        var alertsResolved = await ResolveInactiveAlertsAsync(
            writeContext,
            db,
            dispatcher,
            settings,
            activeIssueKeys,
            evaluatedPairKeys,
            evaluatedSymbols,
            resolveAll: false,
            ct);

        return new MLDataQualityCycleResult(
            settings,
            null,
            evaluatedPairKeys.Count,
            pairsSkipped,
            issuesDetected,
            alertsDispatched,
            alertsResolved,
            loadedPairs.Truncated);
    }

    private async Task<LoadActivePairsResult> LoadActivePairsAsync(
        DbContext db,
        MLDataQualityWorkerSettings settings,
        CancellationToken ct)
    {
        var rawPairs = await db.Set<MLModel>()
            .Where(model => model.IsActive && !model.IsDeleted)
            .OrderBy(model => model.Symbol)
            .ThenBy(model => model.Timeframe)
            .Select(model => new ActivePairProjection(model.Symbol, model.Timeframe))
            .Take(settings.MaxPairsPerCycle + 1)
            .AsNoTracking()
            .ToListAsync(ct);

        var truncated = rawPairs.Count > settings.MaxPairsPerCycle;
        if (truncated)
            rawPairs.RemoveAt(rawPairs.Count - 1);

        var pairs = new List<ActiveModelPair>(rawPairs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invalidPairsSkipped = 0;

        foreach (var rawPair in rawPairs)
        {
            var symbol = NormalizeSymbol(rawPair.Symbol);
            if (symbol.Length == 0 || symbol.Length > 10)
            {
                invalidPairsSkipped++;
                continue;
            }

            var key = PairKey(symbol, rawPair.Timeframe);
            if (seen.Add(key))
                pairs.Add(new ActiveModelPair(symbol, rawPair.Timeframe));
        }

        return new LoadActivePairsResult(pairs, invalidPairsSkipped, truncated);
    }

    private async Task<PairEvaluation> EvaluateCandleFeedAsync(
        DbContext db,
        ActiveModelPair pair,
        DateTime nowUtc,
        MLDataQualityWorkerSettings settings,
        CancellationToken ct)
    {
        if (!TryGetBarDuration(pair.Timeframe, out var barDuration))
            return PairEvaluation.Skipped("unsupported_timeframe");

        var candles = await db.Set<Candle>()
            .Where(candle => candle.Symbol == pair.Symbol
                          && candle.Timeframe == pair.Timeframe
                          && candle.IsClosed
                          && !candle.IsDeleted)
            .OrderByDescending(candle => candle.Timestamp)
            .Take(settings.SpikeLookbackBars + 1)
            .AsNoTracking()
            .Select(candle => new CandleSample(
                candle.Timestamp,
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close))
            .ToListAsync(ct);

        var issues = new List<DataQualityIssue>();
        if (candles.Count == 0)
        {
            issues.Add(CreateIssue(
                "data_quality_missing_candles",
                pair.Symbol,
                pair.Timeframe,
                AlertSeverity.High,
                "No closed candles exist for an active ML model feed.",
                settings,
                nowUtc,
                new Dictionary<string, object?>
                {
                    ["expectedBarSeconds"] = barDuration.TotalSeconds
                }));

            return PairEvaluation.Completed(issues);
        }

        var latest = candles[0];
        var futureSkew = latest.Timestamp - nowUtc;
        if (futureSkew > settings.FutureTimestampTolerance)
        {
            issues.Add(CreateIssue(
                "data_quality_future_candle",
                pair.Symbol,
                pair.Timeframe,
                AlertSeverity.High,
                "Latest closed candle timestamp is in the future.",
                settings,
                nowUtc,
                new Dictionary<string, object?>
                {
                    ["candleTimestamp"] = latest.Timestamp,
                    ["futureSkewSeconds"] = futureSkew.TotalSeconds,
                    ["toleranceSeconds"] = settings.FutureTimestampTolerance.TotalSeconds
                }));
        }
        else
        {
            var candleAgeSeconds = Math.Max(0, (nowUtc - latest.Timestamp).TotalSeconds);
            var gapThresholdSeconds = settings.GapMultiplier * barDuration.TotalSeconds;
            _metrics?.MLDataQualityGapAgeSeconds.Record(
                candleAgeSeconds,
                Tag("symbol", pair.Symbol),
                Tag("timeframe", pair.Timeframe));

            if (candleAgeSeconds > gapThresholdSeconds)
            {
                issues.Add(CreateIssue(
                    "data_quality_gap",
                    pair.Symbol,
                    pair.Timeframe,
                    AlertSeverity.Medium,
                    "Latest closed candle is older than the configured gap threshold.",
                    settings,
                    nowUtc,
                    new Dictionary<string, object?>
                    {
                        ["secondsSinceLastBar"] = candleAgeSeconds,
                        ["gapThresholdSeconds"] = gapThresholdSeconds,
                        ["expectedBarSeconds"] = barDuration.TotalSeconds,
                        ["lastCandleTimestamp"] = latest.Timestamp
                    }));
            }
        }

        if (!IsValidCandlePrice(latest))
        {
            issues.Add(CreateIssue(
                "data_quality_invalid_candle",
                pair.Symbol,
                pair.Timeframe,
                AlertSeverity.High,
                "Latest closed candle contains invalid OHLC prices.",
                settings,
                nowUtc,
                new Dictionary<string, object?>
                {
                    ["candleTimestamp"] = latest.Timestamp,
                    ["open"] = latest.Open,
                    ["high"] = latest.High,
                    ["low"] = latest.Low,
                    ["close"] = latest.Close
                }));

            return PairEvaluation.Completed(issues);
        }

        AddSpikeIssueIfNeeded(candles, pair, nowUtc, settings, issues);
        return PairEvaluation.Completed(issues);
    }

    private void AddSpikeIssueIfNeeded(
        IReadOnlyList<CandleSample> candles,
        ActiveModelPair pair,
        DateTime nowUtc,
        MLDataQualityWorkerSettings settings,
        List<DataQualityIssue> issues)
    {
        if (candles.Count < settings.MinSpikeBaselineBars + 1)
            return;

        var baseline = candles
            .Skip(1)
            .Where(IsValidCandlePrice)
            .Select(candle => (double)candle.Close)
            .Take(settings.SpikeLookbackBars)
            .ToList();

        if (baseline.Count < settings.MinSpikeBaselineBars)
            return;

        var latestClose = (double)candles[0].Close;
        var mean = baseline.Average();
        var variance = baseline.Average(value => (value - mean) * (value - mean));
        var stdDev = Math.Sqrt(variance);

        if (stdDev <= StdEpsilon)
            return;

        var zScore = Math.Abs(latestClose - mean) / stdDev;
        _metrics?.MLDataQualitySpikeZScore.Record(
            zScore,
            Tag("symbol", pair.Symbol),
            Tag("timeframe", pair.Timeframe));

        if (zScore < settings.SpikeSigmas)
            return;

        issues.Add(CreateIssue(
            "data_quality_spike",
            pair.Symbol,
            pair.Timeframe,
            AlertSeverity.High,
            "Latest closed candle close is an anomalous rolling z-score outlier.",
            settings,
            nowUtc,
            new Dictionary<string, object?>
            {
                ["latestClose"] = latestClose,
                ["rollingMean"] = mean,
                ["rollingStdDev"] = stdDev,
                ["zScore"] = zScore,
                ["spikeThreshold"] = settings.SpikeSigmas,
                ["baselineBars"] = baseline.Count,
                ["candleTimestamp"] = candles[0].Timestamp
            }));
    }

    private async Task<IReadOnlyList<DataQualityIssue>> EvaluateLivePriceAsync(
        DbContext db,
        string symbol,
        DateTime nowUtc,
        MLDataQualityWorkerSettings settings,
        CancellationToken ct)
    {
        var livePrice = await db.Set<LivePrice>()
            .Where(price => price.Symbol == symbol)
            .OrderByDescending(price => price.Timestamp)
            .AsNoTracking()
            .Select(price => new LivePriceSample(price.Timestamp, price.Bid, price.Ask))
            .FirstOrDefaultAsync(ct);

        var issues = new List<DataQualityIssue>();
        if (livePrice is null)
        {
            issues.Add(CreateIssue(
                "live_price_missing",
                symbol,
                timeframe: null,
                AlertSeverity.High,
                "No persisted live price snapshot exists for an active ML model symbol.",
                settings,
                nowUtc,
                new Dictionary<string, object?>()));
            return issues;
        }

        if (livePrice.Bid <= 0 || livePrice.Ask <= 0 || livePrice.Ask < livePrice.Bid)
        {
            issues.Add(CreateIssue(
                "live_price_invalid",
                symbol,
                timeframe: null,
                AlertSeverity.High,
                "Persisted live price contains invalid bid/ask values.",
                settings,
                nowUtc,
                new Dictionary<string, object?>
                {
                    ["bid"] = livePrice.Bid,
                    ["ask"] = livePrice.Ask,
                    ["livePriceTimestamp"] = livePrice.Timestamp
                }));
        }

        var futureSkew = livePrice.Timestamp - nowUtc;
        if (futureSkew > settings.FutureTimestampTolerance)
        {
            issues.Add(CreateIssue(
                "live_price_future_timestamp",
                symbol,
                timeframe: null,
                AlertSeverity.High,
                "Persisted live price timestamp is in the future.",
                settings,
                nowUtc,
                new Dictionary<string, object?>
                {
                    ["livePriceTimestamp"] = livePrice.Timestamp,
                    ["futureSkewSeconds"] = futureSkew.TotalSeconds,
                    ["toleranceSeconds"] = settings.FutureTimestampTolerance.TotalSeconds
                }));

            return issues;
        }

        var ageSeconds = Math.Max(0, (nowUtc - livePrice.Timestamp).TotalSeconds);
        _metrics?.MLDataQualityLivePriceAgeSeconds.Record(ageSeconds, Tag("symbol", symbol));

        if (ageSeconds > settings.LivePriceStaleness.TotalSeconds)
        {
            issues.Add(CreateIssue(
                "live_price_stale",
                symbol,
                timeframe: null,
                AlertSeverity.Medium,
                "Persisted live price snapshot is older than the configured staleness threshold.",
                settings,
                nowUtc,
                new Dictionary<string, object?>
                {
                    ["livePriceTimestamp"] = livePrice.Timestamp,
                    ["ageSeconds"] = ageSeconds,
                    ["stalenessThresholdSeconds"] = settings.LivePriceStaleness.TotalSeconds
                }));
        }

        return issues;
    }

    private async Task<bool> UpsertAndDispatchAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        DataQualityIssue issue,
        MLDataQualityWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(existing => existing.AlertType == AlertType.DataQualityIssue
                                          && existing.IsActive
                                          && !existing.IsDeleted
                                          && existing.DeduplicationKey == issue.DeduplicationKey,
                ct);

        var created = alert is null;
        if (created)
        {
            alert = new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                DeduplicationKey = issue.DeduplicationKey
            };
            db.Set<Alert>().Add(alert);
        }

        var previousTriggeredAt = alert!.LastTriggeredAt;
        ApplyIssueToAlert(alert, issue, settings);

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (created)
        {
            db.Entry(alert).State = EntityState.Detached;
            var existing = await db.Set<Alert>()
                .FirstOrDefaultAsync(a => a.AlertType == AlertType.DataQualityIssue
                                       && a.IsActive
                                       && !a.IsDeleted
                                       && a.DeduplicationKey == issue.DeduplicationKey,
                    ct);

            if (existing is null)
                throw;

            ApplyIssueToAlert(existing, issue, settings);
            await writeContext.SaveChangesAsync(ct);
            _logger.LogDebug(
                ex,
                "{Worker}: recovered duplicate alert insert race for {DeduplicationKey}.",
                WorkerName,
                issue.DeduplicationKey);
            return false;
        }

        if (IsWithinCooldown(previousTriggeredAt, nowUtc, settings.AlertCooldown))
            return false;

        if (dispatcher is null)
            return false;

        var lastTriggeredBeforeDispatch = alert.LastTriggeredAt;
        try
        {
            await dispatcher.DispatchAsync(alert, issue.Message, ct);
            await writeContext.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch alert {DeduplicationKey}.",
                WorkerName,
                issue.DeduplicationKey);
            return false;
        }

        var dispatched = alert.LastTriggeredAt.HasValue
                         && alert.LastTriggeredAt != lastTriggeredBeforeDispatch;
        if (dispatched)
        {
            _metrics?.MLDataQualityAlertsDispatched.Add(
                1,
                Tag("reason", issue.Reason),
                Tag("symbol", issue.Symbol),
                Tag("timeframe", issue.Timeframe?.ToString() ?? SymbolScope));
        }

        return dispatched;
    }

    private async Task<int> ResolveInactiveAlertsAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDataQualityWorkerSettings settings,
        IReadOnlySet<string> activeIssueKeys,
        IReadOnlySet<string> evaluatedPairKeys,
        IReadOnlySet<string> evaluatedSymbols,
        bool resolveAll,
        CancellationToken ct)
    {
        var alerts = await db.Set<Alert>()
            .Where(alert => alert.AlertType == AlertType.DataQualityIssue
                         && alert.IsActive
                         && !alert.IsDeleted
                         && alert.DeduplicationKey != null
                         && alert.DeduplicationKey.StartsWith(AlertDedupPrefix))
            .ToListAsync(ct);

        var staleAlerts = alerts
            .Where(alert => !activeIssueKeys.Contains(alert.DeduplicationKey!)
                         && IsWithinEvaluationScope(
                             alert.DeduplicationKey!,
                             evaluatedPairKeys,
                             evaluatedSymbols,
                             resolveAll))
            .ToList();

        if (staleAlerts.Count == 0)
            return 0;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var alert in staleAlerts)
        {
            ct.ThrowIfCancellationRequested();

            alert.IsActive = false;
            alert.CooldownSeconds = (int)settings.AlertCooldown.TotalSeconds;

            if (dispatcher is not null)
            {
                try
                {
                    await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "{Worker}: failed to dispatch data-quality resolution for {DeduplicationKey}.",
                        WorkerName,
                        alert.DeduplicationKey);
                }
            }

            alert.AutoResolvedAt ??= nowUtc;
        }

        await writeContext.SaveChangesAsync(ct);
        _metrics?.MLDataQualityAlertsResolved.Add(staleAlerts.Count);
        return staleAlerts.Count;
    }

    private async Task<MLDataQualityWorkerSettings> LoadRuntimeSettingsAsync(
        DbContext db,
        MLDataQualityWorkerSettings defaults,
        CancellationToken ct)
    {
        var keys = new[]
        {
            CK_Enabled,
            CK_PollSecs,
            CK_GapMult,
            CK_SpikeSigmas,
            CK_SpikeBars,
            CK_MinSpikeBaselineBars,
            CK_LiveStale,
            CK_FutureTimestampTolerance,
            CK_MaxPairs,
            CK_LockTimeout,
            CK_AlertCooldown,
            CK_AlertDest,
        };

        var config = await db.Set<EngineConfig>()
            .Where(entry => keys.Contains(entry.Key) && !entry.IsDeleted)
            .AsNoTracking()
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Value, ct);

        var spikeLookbackBars = GetInt(config, CK_SpikeBars, defaults.SpikeLookbackBars, 3, 10_000);
        var minSpikeBaselineBars = Math.Min(
            GetInt(config, CK_MinSpikeBaselineBars, defaults.MinSpikeBaselineBars, 3, 10_000),
            spikeLookbackBars);

        return defaults with
        {
            Enabled = GetBool(config, CK_Enabled, defaults.Enabled),
            PollInterval = TimeSpan.FromSeconds(GetInt(config, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds, 30, 86_400)),
            GapMultiplier = GetDouble(config, CK_GapMult, defaults.GapMultiplier, 1.0, 100.0),
            SpikeSigmas = GetDouble(config, CK_SpikeSigmas, defaults.SpikeSigmas, 1.0, 20.0),
            SpikeLookbackBars = spikeLookbackBars,
            MinSpikeBaselineBars = minSpikeBaselineBars,
            LivePriceStaleness = TimeSpan.FromSeconds(GetInt(config, CK_LiveStale, (int)defaults.LivePriceStaleness.TotalSeconds, 1, 86_400)),
            FutureTimestampTolerance = TimeSpan.FromSeconds(GetInt(config, CK_FutureTimestampTolerance, (int)defaults.FutureTimestampTolerance.TotalSeconds, 0, 3_600)),
            MaxPairsPerCycle = GetInt(config, CK_MaxPairs, defaults.MaxPairsPerCycle, 1, 10_000),
            LockTimeout = TimeSpan.FromSeconds(GetInt(config, CK_LockTimeout, (int)defaults.LockTimeout.TotalSeconds, 0, 300)),
            AlertCooldown = TimeSpan.FromSeconds(GetInt(config, CK_AlertCooldown, (int)defaults.AlertCooldown.TotalSeconds, 0, 86_400)),
            AlertDestination = GetString(config, CK_AlertDest, defaults.AlertDestination, 100),
        };
    }

    private static MLDataQualityWorkerSettings BuildSettings(MLDataQualityOptions options)
    {
        var spikeLookbackBars = Clamp(options.SpikeLookbackBars, 3, 10_000);
        var minSpikeBaselineBars = Math.Min(
            Clamp(options.MinSpikeBaselineBars, 3, 10_000),
            spikeLookbackBars);

        return new MLDataQualityWorkerSettings
        {
            Enabled = options.Enabled,
            InitialDelay = TimeSpan.FromSeconds(Clamp(options.InitialDelaySeconds, 0, 86_400)),
            PollInterval = TimeSpan.FromSeconds(Clamp(options.PollIntervalSeconds, 30, 86_400)),
            PollJitter = TimeSpan.FromSeconds(Clamp(options.PollJitterSeconds, 0, 86_400)),
            GapMultiplier = ClampFinite(options.GapMultiplier, 1.0, 100.0),
            SpikeSigmas = ClampFinite(options.SpikeSigmas, 1.0, 20.0),
            SpikeLookbackBars = spikeLookbackBars,
            MinSpikeBaselineBars = minSpikeBaselineBars,
            LivePriceStaleness = TimeSpan.FromSeconds(Clamp(options.LivePriceStalenessSeconds, 1, 86_400)),
            FutureTimestampTolerance = TimeSpan.FromSeconds(Clamp(options.FutureTimestampToleranceSeconds, 0, 3_600)),
            MaxPairsPerCycle = Clamp(options.MaxPairsPerCycle, 1, 10_000),
            LockTimeout = TimeSpan.FromSeconds(Clamp(options.LockTimeoutSeconds, 0, 300)),
            AlertCooldown = TimeSpan.FromSeconds(Clamp(options.AlertCooldownSeconds, 0, 86_400)),
            AlertDestination = NormalizeDestination(options.AlertDestination),
        };
    }

    private static void ApplyIssueToAlert(
        Alert alert,
        DataQualityIssue issue,
        MLDataQualityWorkerSettings settings)
    {
        alert.AlertType = AlertType.DataQualityIssue;
        alert.Symbol = issue.Symbol;
        alert.ConditionJson = issue.ConditionJson;
        alert.IsActive = true;
        alert.Severity = issue.Severity;
        alert.DeduplicationKey = issue.DeduplicationKey;
        alert.CooldownSeconds = (int)settings.AlertCooldown.TotalSeconds;
        alert.AutoResolvedAt = null;
    }

    private static DataQualityIssue CreateIssue(
        string reason,
        string symbol,
        Timeframe? timeframe,
        AlertSeverity severity,
        string summary,
        MLDataQualityWorkerSettings settings,
        DateTime nowUtc,
        IReadOnlyDictionary<string, object?> details)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["worker"] = WorkerName,
            ["severity"] = severity.ToString(),
            ["symbol"] = symbol,
            ["timeframe"] = timeframe?.ToString(),
            ["destination"] = settings.AlertDestination,
            ["detectedAt"] = nowUtc
        };

        foreach (var (key, value) in details)
            payload[key] = value;

        var conditionJson = JsonSerializer.Serialize(payload, JsonOptions);
        if (conditionJson.Length > AlertConditionMaxLength)
        {
            conditionJson = JsonSerializer.Serialize(new
            {
                reason,
                worker = WorkerName,
                severity = severity.ToString(),
                symbol,
                timeframe = timeframe?.ToString(),
                destination = settings.AlertDestination,
                detectedAt = nowUtc,
                truncated = true
            }, JsonOptions);
        }

        var scope = timeframe.HasValue ? $"{symbol}/{timeframe}" : symbol;
        return new DataQualityIssue(
            reason,
            symbol,
            timeframe,
            severity,
            conditionJson,
            $"ML data-quality issue '{reason}' for {scope}: {summary}",
            BuildDedupKey(reason, symbol, timeframe));
    }

    private static bool IsWithinCooldown(DateTime? lastTriggeredAt, DateTime nowUtc, TimeSpan cooldown)
    {
        if (!lastTriggeredAt.HasValue || cooldown <= TimeSpan.Zero)
            return false;

        var lastUtc = lastTriggeredAt.Value.Kind switch
        {
            DateTimeKind.Local => lastTriggeredAt.Value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(lastTriggeredAt.Value, DateTimeKind.Utc),
            _ => lastTriggeredAt.Value
        };

        return nowUtc - lastUtc < cooldown;
    }

    private static bool IsWithinEvaluationScope(
        string deduplicationKey,
        IReadOnlySet<string> evaluatedPairKeys,
        IReadOnlySet<string> evaluatedSymbols,
        bool resolveAll)
    {
        if (resolveAll)
            return true;

        if (!deduplicationKey.StartsWith(AlertDedupPrefix, StringComparison.Ordinal))
            return false;

        var parts = deduplicationKey[AlertDedupPrefix.Length..].Split(':');
        if (parts.Length < 3)
            return false;

        var symbol = parts[^2];
        var scope = parts[^1];
        return scope == SymbolScope
            ? evaluatedSymbols.Contains(symbol)
            : evaluatedPairKeys.Contains($"{symbol}:{scope}");
    }

    private static bool TryGetBarDuration(Timeframe timeframe, out TimeSpan duration)
    {
        duration = timeframe switch
        {
            Timeframe.M1 => TimeSpan.FromMinutes(1),
            Timeframe.M5 => TimeSpan.FromMinutes(5),
            Timeframe.M15 => TimeSpan.FromMinutes(15),
            Timeframe.H1 => TimeSpan.FromHours(1),
            Timeframe.H4 => TimeSpan.FromHours(4),
            Timeframe.D1 => TimeSpan.FromDays(1),
            _ => TimeSpan.Zero
        };

        return duration > TimeSpan.Zero;
    }

    private static bool IsValidCandlePrice(CandleSample candle)
        => candle.Open > 0
           && candle.High > 0
           && candle.Low > 0
           && candle.Close > 0
           && candle.High >= candle.Open
           && candle.High >= candle.Close
           && candle.Low <= candle.Open
           && candle.Low <= candle.Close
           && candle.High >= candle.Low;

    private static string BuildDedupKey(string reason, string symbol, Timeframe? timeframe)
        => $"{AlertDedupPrefix}{reason}:{symbol}:{timeframe?.ToString() ?? SymbolScope}";

    private static string PairKey(string symbol, Timeframe timeframe)
        => $"{symbol}:{timeframe}";

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();

    private static string NormalizeDestination(string? destination)
    {
        var value = string.IsNullOrWhiteSpace(destination)
            ? "market-data"
            : destination.Trim();

        return value.Length > 100 ? value[..100] : value;
    }

    private static TimeSpan GetIntervalWithJitter(MLDataQualityWorkerSettings settings)
    {
        if (settings.PollJitter <= TimeSpan.Zero)
            return settings.PollInterval;

        var jitterMs = Random.Shared.NextDouble() * settings.PollJitter.TotalMilliseconds;
        return settings.PollInterval + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static TimeSpan CalculateDelay(TimeSpan baseDelay, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseDelay;

        var multiplier = Math.Min(8, 1 << Math.Min(consecutiveFailures, 3));
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * multiplier);
    }

    private void RecordPairSkipped(string reason)
        => _metrics?.MLDataQualityPairsSkipped.Add(1, Tag("reason", reason));

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLDataQualityCyclesSkipped.Add(1, Tag("reason", reason));

    private void RecordIssueDetected(DataQualityIssue issue)
        => _metrics?.MLDataQualityIssuesDetected.Add(
            1,
            Tag("reason", issue.Reason),
            Tag("symbol", issue.Symbol),
            Tag("timeframe", issue.Timeframe?.ToString() ?? SymbolScope));

    private static bool GetBool(
        IReadOnlyDictionary<string, string> config,
        string key,
        bool defaultValue)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }

    private static int GetInt(
        IReadOnlyDictionary<string, string> config,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        if (!config.TryGetValue(key, out var value)
            || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return Clamp(parsed, min, max);
    }

    private static double GetDouble(
        IReadOnlyDictionary<string, string> config,
        string key,
        double defaultValue,
        double min,
        double max)
    {
        if (!config.TryGetValue(key, out var value)
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return ClampFinite(parsed, min, max);
    }

    private static string GetString(
        IReadOnlyDictionary<string, string> config,
        string key,
        string defaultValue,
        int maxLength)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static double ClampFinite(double value, double min, double max)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private sealed record ActivePairProjection(string Symbol, Timeframe Timeframe);

    private sealed record ActiveModelPair(string Symbol, Timeframe Timeframe);

    private sealed record LoadActivePairsResult(
        List<ActiveModelPair> Pairs,
        int InvalidPairsSkipped,
        bool Truncated);

    private sealed record CandleSample(
        DateTime Timestamp,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close);

    private sealed record LivePriceSample(
        DateTime Timestamp,
        decimal Bid,
        decimal Ask);

    private sealed record PairEvaluation(
        bool Evaluated,
        string? SkipReason,
        IReadOnlyList<DataQualityIssue> Issues)
    {
        public static PairEvaluation Completed(IReadOnlyList<DataQualityIssue> issues)
            => new(true, null, issues);

        public static PairEvaluation Skipped(string reason)
            => new(false, reason, Array.Empty<DataQualityIssue>());
    }

    private sealed record DataQualityIssue(
        string Reason,
        string Symbol,
        Timeframe? Timeframe,
        AlertSeverity Severity,
        string ConditionJson,
        string Message,
        string DeduplicationKey);
}

internal sealed record MLDataQualityWorkerSettings
{
    public bool Enabled { get; init; } = true;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollJitter { get; init; } = TimeSpan.FromSeconds(30);
    public double GapMultiplier { get; init; } = 2.5;
    public double SpikeSigmas { get; init; } = 4.0;
    public int SpikeLookbackBars { get; init; } = 50;
    public int MinSpikeBaselineBars { get; init; } = 20;
    public TimeSpan LivePriceStaleness { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FutureTimestampTolerance { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxPairsPerCycle { get; init; } = 1_000;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AlertCooldown { get; init; } = TimeSpan.FromMinutes(30);
    public string AlertDestination { get; init; } = "market-data";
}

internal sealed record MLDataQualityCycleResult(
    MLDataQualityWorkerSettings Settings,
    string? SkippedReason,
    int PairsEvaluated,
    int PairsSkipped,
    int IssuesDetected,
    int AlertsDispatched,
    int AlertsResolved,
    bool Truncated)
{
    public static MLDataQualityCycleResult Skipped(
        MLDataQualityWorkerSettings settings,
        string reason)
        => new(settings, reason, 0, 0, 0, 0, 0, false);
}
