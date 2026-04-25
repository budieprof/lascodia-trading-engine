using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors per-symbol realized slippage drift against a rolling baseline. A rising
/// slippage trend is a leading indicator that a strategy has grown beyond the market
/// depth it can absorb — the fills degrade before the Sharpe does, so this catches
/// crowding 2–6 weeks earlier than the P&amp;L monitor would.
/// </summary>
/// <remarks>
/// <para>
/// Compares recent-window average slippage (last 7 days) against baseline-window average
/// slippage (prior 30 days) per symbol. When the ratio exceeds the configurable drift
/// threshold, fires an alert via <see cref="IAlertDispatcher"/> with a per-symbol dedupe
/// key and logs a warning. Does not take automatic action — the fix (reduce size, pause
/// strategy) is a human decision that also benefits from TCA context the worker doesn't
/// have.
/// </para>
/// <para>
/// Source data: <see cref="TransactionCostAnalysis.SpreadCost"/> +
/// <see cref="TransactionCostAnalysis.MarketImpactCost"/> rolled up per symbol. Default
/// poll cadence is 30 minutes; the outer loop wakes every 60 s so operator changes to
/// <c>SlippageDrift:PollIntervalSeconds</c> propagate within a minute.
/// </para>
/// </remarks>
public sealed class SlippageDriftWorker : BackgroundService
{
    internal const string WorkerName = nameof(SlippageDriftWorker);

    private const string DistributedLockKey = "workers:slippage-drift:cycle";

    private const string CK_Enabled = "SlippageDrift:Enabled";
    private const string CK_PollSecs = "SlippageDrift:PollIntervalSeconds";
    private const string CK_DriftThreshold = "SlippageDrift:DriftThreshold";
    private const string CK_RecentWindowDays = "SlippageDrift:RecentWindowDays";
    private const string CK_BaselineWindowDays = "SlippageDrift:BaselineWindowDays";
    private const string CK_MinTradesInWindow = "SlippageDrift:MinTradesInWindow";
    private const string CK_LockTimeoutSecs = "SlippageDrift:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSecs = "SlippageDrift:DbCommandTimeoutSeconds";

    // Legacy keys preserved for backward compatibility with operators who already
    // configured the worker before this hardening pass.
    private const string CK_LegacyEnabled = "SlippageDriftWorker:Enabled";
    private const string CK_LegacyDriftThreshold = "SlippageDriftWorker:DriftThreshold";

    private const int DefaultPollSeconds = 1800; // 30 min
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 24 * 60 * 60;

    private const double DefaultDriftThreshold = 1.5; // 50% rise over baseline
    private const double MinDriftThreshold = 1.001;
    private const double MaxDriftThreshold = 100.0;

    private const int DefaultRecentWindowDays = 7;
    private const int MinRecentWindowDays = 1;
    private const int MaxRecentWindowDays = 90;

    private const int DefaultBaselineWindowDays = 30;
    private const int MinBaselineWindowDays = 7;
    private const int MaxBaselineWindowDays = 365;

    private const int DefaultMinTradesInWindow = 20;
    private const int MinMinTradesInWindow = 1;
    private const int MaxMinTradesInWindow = 10_000;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultDbCommandTimeoutSeconds = 60;
    private const int MinDbCommandTimeoutSeconds = 5;
    private const int MaxDbCommandTimeoutSeconds = 600;

    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlippageDriftWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertDispatcher? _alertDispatcher;

    private long _consecutiveFailuresField;
    private int _missingDistributedLockWarningEmitted;

    public SlippageDriftWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SlippageDriftWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _alertDispatcher = alertDispatcher;
    }

    private int ConsecutiveFailures
    {
        get => (int)Interlocked.Read(ref _consecutiveFailuresField);
        set => Interlocked.Exchange(ref _consecutiveFailuresField, value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Detects per-symbol slippage drift vs a rolling baseline; alerts on crowding signals before P&L degrades.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(DefaultPollSeconds);

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
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

                if (lastSuccessUtc != DateTime.MinValue)
                {
                    _metrics?.SlippageDriftTimeSinceLastSuccessSec.Record(
                        (nowUtc - lastSuccessUtc).TotalSeconds);
                }

                bool dueForCycle = nowUtc - lastCycleStartUtc >= currentPollInterval;

                if (dueForCycle)
                {
                    long cycleStarted = Stopwatch.GetTimestamp();
                    lastCycleStartUtc = nowUtc;

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Settings.PollInterval;

                        long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                        _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            durationMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.SlippageDriftCycleDurationMs.Record(durationMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: symbols evaluated={Evaluated}, drifts detected={Drifts}.",
                                WorkerName, result.SymbolsEvaluated, result.DriftsDetected);
                        }

                        var prevFailures = ConsecutiveFailures;
                        if (prevFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, prevFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName, prevFailures);
                        }

                        ConsecutiveFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveFailuresField);
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "slippage_drift_cycle"));
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                try
                {
                    await Task.Delay(CalculateDelay(WakeInterval, ConsecutiveFailures), _timeProvider, stoppingToken);
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
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<SlippageDriftCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readCtx.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        ApplyCommandTimeout(db, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            _metrics?.SlippageDriftCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return SlippageDriftCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.SlippageDriftLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate slippage-drift cycles are possible in multi-instance deployments.",
                    WorkerName);
            }
            return await RunCycleCoreAsync(db, settings, ct);
        }

        var cycleLock = await _distributedLock.TryAcquireAsync(
            DistributedLockKey,
            TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
            ct);

        if (cycleLock is null)
        {
            _metrics?.SlippageDriftLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "busy"));
            _metrics?.SlippageDriftCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "lock_busy"));
            return SlippageDriftCycleResult.Skipped(settings, "lock_busy");
        }

        _metrics?.SlippageDriftLockAttempts.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "acquired"));

        await using (cycleLock)
        {
            return await RunCycleCoreAsync(db, settings, ct);
        }
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return baseInterval <= TimeSpan.Zero
                ? WakeInterval
                : baseInterval;
        }

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException) { /* provider lacks support — skip */ }
    }

    private async Task<SlippageDriftCycleResult> RunCycleCoreAsync(
        DbContext db,
        SlippageDriftWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var recentCutoff = nowUtc.AddDays(-settings.RecentWindowDays);
        var baselineCutoff = nowUtc.AddDays(-(settings.RecentWindowDays + settings.BaselineWindowDays));

        var recentRows = await db.Set<TransactionCostAnalysis>()
            .AsNoTracking()
            .Where(t => !t.IsDeleted && t.AnalyzedAt >= recentCutoff)
            .GroupBy(t => t.Symbol)
            .Select(g => new SlippageRollup(g.Key, g.Count(), g.Average(t => (double)(t.SpreadCost + t.MarketImpactCost))))
            .ToListAsync(ct);

        var baselineRows = await db.Set<TransactionCostAnalysis>()
            .AsNoTracking()
            .Where(t => !t.IsDeleted && t.AnalyzedAt >= baselineCutoff && t.AnalyzedAt < recentCutoff)
            .GroupBy(t => t.Symbol)
            .Select(g => new SlippageRollup(g.Key, g.Count(), g.Average(t => (double)(t.SpreadCost + t.MarketImpactCost))))
            .ToListAsync(ct);

        var baselineMap = baselineRows.ToDictionary(r => r.Symbol);

        int symbolsEvaluated = 0;
        int driftsDetected = 0;

        foreach (var recent in recentRows)
        {
            ct.ThrowIfCancellationRequested();

            if (recent.Count < settings.MinTradesInWindow) continue;
            if (!baselineMap.TryGetValue(recent.Symbol, out var baseline)) continue;
            if (baseline.Count < settings.MinTradesInWindow) continue;
            if (baseline.AvgSlippage <= 0) continue;

            symbolsEvaluated++;
            double ratio = recent.AvgSlippage / baseline.AvgSlippage;

            _metrics?.SlippageDriftRatio.Record(
                ratio,
                new KeyValuePair<string, object?>("symbol", recent.Symbol));

            if (ratio >= settings.DriftThreshold)
            {
                driftsDetected++;
                _metrics?.SlippageDriftsDetected.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", recent.Symbol));

                _logger.LogWarning(
                    "{Worker}: {Symbol} slippage drift {Ratio:F2}x over baseline " +
                    "(recent={Recent:F5} over {RecentN} trades, baseline={Baseline:F5} over {BaselineN} trades). " +
                    "Possible strategy crowding — review position sizing and capacity.",
                    WorkerName, recent.Symbol, ratio,
                    recent.AvgSlippage, recent.Count,
                    baseline.AvgSlippage, baseline.Count);

                await DispatchDriftAlertAsync(recent.Symbol, ratio, recent, baseline, settings, ct);
            }
        }

        return new SlippageDriftCycleResult(
            settings,
            SkippedReason: null,
            SymbolsEvaluated: symbolsEvaluated,
            DriftsDetected: driftsDetected);
    }

    private async Task DispatchDriftAlertAsync(
        string symbol,
        double ratio,
        SlippageRollup recent,
        SlippageRollup baseline,
        SlippageDriftWorkerSettings settings,
        CancellationToken ct)
    {
        if (_alertDispatcher is null)
            return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                writeCtx, AlertCooldownDefaults.CK_MLDrift, AlertCooldownDefaults.Default_MLDrift, ct);

            string conditionJson = JsonSerializer.Serialize(new
            {
                detector = "SlippageDrift",
                symbol,
                ratio,
                recentAvgSlippage = recent.AvgSlippage,
                recentTradeCount = recent.Count,
                baselineAvgSlippage = baseline.AvgSlippage,
                baselineTradeCount = baseline.Count,
                threshold = settings.DriftThreshold,
                detectedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            });

            var alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                Severity = AlertSeverity.High,
                Symbol = symbol,
                DeduplicationKey = $"slippage-drift:{symbol}",
                CooldownSeconds = cooldownSec,
                ConditionJson = conditionJson,
                IsActive = true,
            };

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Slippage drift on {0}: {1:F2}x over baseline (recent {2:F5} / {3} trades vs baseline {4:F5} / {5} trades). Possible strategy crowding.",
                symbol, ratio, recent.AvgSlippage, recent.Count, baseline.AvgSlippage, baseline.Count);

            await _alertDispatcher.DispatchAsync(alert, message, ct);
            _metrics?.SlippageDriftAlertsDispatched.Add(
                1, new KeyValuePair<string, object?>("symbol", symbol));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch slippage-drift alert for {Symbol}.",
                WorkerName, symbol);
        }
    }

    private static async Task<SlippageDriftWorkerSettings> LoadSettingsAsync(
        DbContext db,
        CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled, CK_LegacyEnabled,
            CK_PollSecs,
            CK_DriftThreshold, CK_LegacyDriftThreshold,
            CK_RecentWindowDays, CK_BaselineWindowDays,
            CK_MinTradesInWindow,
            CK_LockTimeoutSecs,
            CK_DbCommandTimeoutSecs,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        bool enabled = GetBool(values, CK_Enabled, GetBool(values, CK_LegacyEnabled, true));
        double driftThreshold = GetDouble(values, CK_DriftThreshold,
            GetDouble(values, CK_LegacyDriftThreshold, DefaultDriftThreshold));

        return new SlippageDriftWorkerSettings(
            Enabled: enabled,
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds), DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            DriftThreshold: ClampDoubleAbove1(driftThreshold, DefaultDriftThreshold, MinDriftThreshold, MaxDriftThreshold),
            RecentWindowDays: ClampInt(GetInt(values, CK_RecentWindowDays, DefaultRecentWindowDays), DefaultRecentWindowDays, MinRecentWindowDays, MaxRecentWindowDays),
            BaselineWindowDays: ClampInt(GetInt(values, CK_BaselineWindowDays, DefaultBaselineWindowDays), DefaultBaselineWindowDays, MinBaselineWindowDays, MaxBaselineWindowDays),
            MinTradesInWindow: ClampInt(GetInt(values, CK_MinTradesInWindow, DefaultMinTradesInWindow), DefaultMinTradesInWindow, MinMinTradesInWindow, MaxMinTradesInWindow),
            LockTimeoutSeconds: ClampInt(GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds), DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            DbCommandTimeoutSeconds: ClampInt(GetInt(values, CK_DbCommandTimeoutSecs, DefaultDbCommandTimeoutSeconds), DefaultDbCommandTimeoutSeconds, MinDbCommandTimeoutSeconds, MaxDbCommandTimeoutSeconds));
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (bool.TryParse(raw, out var parsedBool)) return parsedBool;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;
        return defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
        => values.TryGetValue(key, out var raw) &&
           int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : defaultValue;

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
        => values.TryGetValue(key, out var raw) &&
           double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : defaultValue;

    private static int ClampInt(int value, int fallback, int min, int max)
        => value <= 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private static double ClampDoubleAbove1(double value, double fallback, double min, double max)
        => !double.IsFinite(value) || value <= 1.0
            ? fallback
            : Math.Min(Math.Max(value, min), max);

    private readonly record struct SlippageRollup(string Symbol, int Count, double AvgSlippage);

    internal readonly record struct SlippageDriftWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        double DriftThreshold,
        int RecentWindowDays,
        int BaselineWindowDays,
        int MinTradesInWindow,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds);

    internal readonly record struct SlippageDriftCycleResult(
        SlippageDriftWorkerSettings Settings,
        string? SkippedReason,
        int SymbolsEvaluated,
        int DriftsDetected)
    {
        public static SlippageDriftCycleResult Skipped(
            SlippageDriftWorkerSettings settings, string reason)
            => new(settings, reason, 0, 0);
    }
}
