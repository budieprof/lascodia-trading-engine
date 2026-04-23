using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Hourly worker that compares engine-tracked account equity against the latest
/// broker-reported equity from <see cref="BrokerAccountSnapshot"/> records.
/// Alerts when the variance exceeds a configurable threshold (default 0.5%).
/// Pauses all trading if variance exceeds the critical threshold (default 2%).
/// </summary>
public sealed class BrokerPnLReconciliationWorker : BackgroundService
{
    internal const string WorkerName = nameof(BrokerPnLReconciliationWorker);

    private const string ConfigPrefix = "BrokerPnLReconciliation:";
    private const string EnabledConfigKey = ConfigPrefix + "Enabled";
    private const string IntervalMinutesConfigKey = ConfigPrefix + "IntervalMinutes";
    private const string InitialDelaySecondsConfigKey = ConfigPrefix + "InitialDelaySeconds";
    private const string MaxSnapshotAgeMinutesConfigKey = ConfigPrefix + "MaxSnapshotAgeMinutes";
    private const string FutureSnapshotToleranceMinutesConfigKey = ConfigPrefix + "FutureSnapshotToleranceMinutes";
    private const string WarningVariancePctConfigKey = ConfigPrefix + "WarningVariancePct";
    private const string CriticalVariancePctConfigKey = ConfigPrefix + "CriticalVariancePct";
    private const string MinimumBrokerEquityConfigKey = ConfigPrefix + "MinimumBrokerEquity";
    private const string ActiveAccountsOnlyConfigKey = ConfigPrefix + "ActiveAccountsOnly";
    private const string AutoKillSwitchOnCriticalConfigKey = ConfigPrefix + "AutoKillSwitchOnCritical";
    private const string RequiredCriticalAccountsForGlobalKillConfigKey = ConfigPrefix + "RequiredCriticalAccountsForGlobalKill";
    private const string ReconcileBalanceConfigKey = ConfigPrefix + "ReconcileBalance";
    private const string AlertCooldownSecondsConfigKey = ConfigPrefix + "AlertCooldownSeconds";
    private const string LockTimeoutSecondsConfigKey = ConfigPrefix + "LockTimeoutSeconds";

    private const string LegacyWarningVarianceConfigKey = ConfigPrefix + "WarningVarianceThreshold";
    private const string LegacyCriticalVarianceConfigKey = ConfigPrefix + "CriticalVarianceThreshold";

    private const string DistributedLockKey = "workers:broker-pnl-reconciliation";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BrokerPnLReconciliationWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly IKillSwitchService _killSwitch;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock _distributedLock;

    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultMaxSnapshotAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan DefaultFutureSnapshotTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromHours(4);

    private const double DefaultWarningVarianceFraction = 0.005; // 0.5%
    private const double DefaultCriticalVarianceFraction = 0.02; // 2%
    private const decimal DefaultMinimumBrokerEquity = 0.01m;
    private const int DefaultAlertCooldownSeconds = 3600;

    private int _consecutiveFailures;
    private bool _legacyWarningVarianceKeyLogged;
    private bool _legacyCriticalVarianceKeyLogged;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public BrokerPnLReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<BrokerPnLReconciliationWorker> logger,
        TradingMetrics metrics,
        IKillSwitchService killSwitch,
        IDistributedLock distributedLock,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(killSwitch);
        ArgumentNullException.ThrowIfNull(distributedLock);

        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _killSwitch = killSwitch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} starting.", WorkerName);

        var settings = BrokerPnLReconciliationSettings.Default;
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Reconciles engine-tracked account equity against EA broker account snapshots.",
            settings.Interval);

        try
        {
            using var startupScope = _scopeFactory.CreateScope();
            var startupDb = startupScope.ServiceProvider
                .GetRequiredService<IWriteApplicationDbContext>()
                .GetDbContext();
            settings = await LoadSettingsAsync(startupDb, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to read startup settings; using defaults for initial delay.",
                WorkerName);
        }

        if (settings.InitialDelay > TimeSpan.Zero)
            await Task.Delay(settings.InitialDelay, _timeProvider, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            long cycleStarted = Stopwatch.GetTimestamp();
            var nextDelay = settings.Interval;

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                var result = await ReconcileAsync(stoppingToken);
                settings = result.Settings;
                nextDelay = settings.Interval;

                _consecutiveFailures = 0;
                _healthMonitor?.RecordBacklogDepth(WorkerName, result.CriticalCount + result.WarningCount);
                _healthMonitor?.RecordCycleSuccess(
                    WorkerName,
                    (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds);

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
                        "{Worker}: cycle complete; accounts={Accounts}, checked={Checked}, ok={Ok}, warnings={Warnings}, critical={Critical}, invalid={Invalid}, currencyMismatch={CurrencyMismatch}, missingFreshSnapshots={Missing}, killSwitchActivated={KillSwitchActivated}.",
                        WorkerName,
                        result.AccountCount,
                        result.CheckedCount,
                        result.OkCount,
                        result.WarningCount,
                        result.CriticalCount,
                        result.InvalidSnapshotCount,
                        result.CurrencyMismatchCount,
                        result.MissingFreshSnapshotCount,
                        result.KillSwitchActivated);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(
                    ex,
                    "{Worker}: error during reconciliation cycle (consecutive failures: {Failures}).",
                    WorkerName,
                    _consecutiveFailures);
                _metrics.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "reconciliation_cycle"));
            }

            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Max(
                    nextDelay.TotalSeconds,
                    Math.Min(
                        nextDelay.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                        MaxFailureBackoff.TotalSeconds)))
                : nextDelay;

            await Task.Delay(delay, _timeProvider, stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("{Worker} stopped.", WorkerName);
    }

    internal async Task<BrokerPnLReconciliationResult> ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(writeDb, ct);

        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Reconciles engine-tracked account equity against EA broker account snapshots.",
            settings.Interval);

        if (!settings.Enabled)
            return BrokerPnLReconciliationResult.Skipped(settings, "disabled");

        await using var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, settings.LockTimeout, ct);

        if (cycleLock is null)
            return BrokerPnLReconciliationResult.Skipped(settings, "lock_busy");

        return await ReconcileCoreAsync(scope.ServiceProvider, writeContext, writeDb, settings, ct);
    }

    private async Task<BrokerPnLReconciliationResult> ReconcileCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext writeDb,
        BrokerPnLReconciliationSettings settings,
        CancellationToken ct)
    {
        var nowUtc = UtcNow;
        var snapshotCutoffUtc = nowUtc.Subtract(settings.MaxSnapshotAge);
        var latestAllowedSnapshotUtc = nowUtc.Add(settings.FutureSnapshotTolerance);

        var accounts = await writeDb.Set<TradingAccount>()
            .AsNoTracking()
            .Where(a => !a.IsDeleted && (!settings.ActiveAccountsOnly || a.IsActive))
            .Select(a => new TradingAccountProjection(
                a.Id,
                a.AccountId,
                a.BrokerServer,
                a.Currency,
                a.Equity,
                a.Balance,
                a.IsActive))
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            _logger.LogDebug("{Worker}: no eligible trading accounts to reconcile.", WorkerName);
            return new BrokerPnLReconciliationResult(settings, AccountCount: 0);
        }

        var accountIds = accounts.Select(a => a.Id).ToList();
        var latestSnapshots = await writeDb.Set<BrokerAccountSnapshot>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted
                     && accountIds.Contains(s.TradingAccountId)
                     && s.ReportedAt >= snapshotCutoffUtc
                     && s.ReportedAt <= latestAllowedSnapshotUtc)
            .GroupBy(s => s.TradingAccountId)
            .Select(g => g
                .OrderByDescending(s => s.ReportedAt)
                .ThenByDescending(s => s.Id)
                .Select(s => new BrokerAccountSnapshotProjection(
                    s.Id,
                    s.TradingAccountId,
                    s.InstanceId,
                    s.Balance,
                    s.Equity,
                    s.Currency,
                    s.ReportedAt))
                .First())
            .ToListAsync(ct);

        var snapshotsByAccount = latestSnapshots.ToDictionary(s => s.TradingAccountId);
        int missingFreshSnapshots = accounts.Count(a => !snapshotsByAccount.ContainsKey(a.Id));

        // Prefetch all currently-active BrokerPnL:* alert dedup keys so per-account resolve calls
        // can skip the DB when there's nothing to resolve. Updated in-memory as we upsert/resolve
        // within the cycle so membership stays accurate.
        var activeAlertKeys = await writeDb.Set<Alert>()
            .AsNoTracking()
            .Where(a => !a.IsDeleted
                     && a.IsActive
                     && a.DeduplicationKey != null
                     && a.DeduplicationKey.StartsWith("BrokerPnL:"))
            .Select(a => a.DeduplicationKey!)
            .ToListAsync(ct);
        var activeKeySet = new HashSet<string>(activeAlertKeys, StringComparer.Ordinal);

        if (latestSnapshots.Count == 0)
        {
            _logger.LogWarning(
                "{Worker}: no fresh broker account snapshots found for {AccountCount} eligible account(s); maxSnapshotAge={MaxAgeMinutes:F0}m.",
                WorkerName,
                accounts.Count,
                settings.MaxSnapshotAge.TotalMinutes);

            foreach (var account in accounts)
            {
                _metrics.BrokerReconciliationOutcomes.Add(
                    1,
                    new KeyValuePair<string, object?>("metric", "equity"),
                    new KeyValuePair<string, object?>("outcome", "stale"));

                // Without fresh data we cannot re-evaluate variance — resolve any stale variance
                // alerts so they don't imply a condition we can no longer confirm.
                await ResolveAlertsAsync(
                    serviceProvider, writeContext, writeDb,
                    AllVarianceAndEquityAlertKeys(account.Id),
                    nowUtc, ct, activeKeySet);

                await UpsertAndDispatchAlertAsync(
                    serviceProvider, writeContext, writeDb,
                    BuildStaleSnapshotDedupKey(account.Id),
                    AlertType.DataQualityIssue,
                    AlertSeverity.High,
                    BuildStaleSnapshotConditionJson(account, settings, nowUtc),
                    $"Broker PnL reconciliation has no fresh broker snapshot for account {account.Id} ({account.AccountId}/{account.BrokerServer}); max snapshot age is {settings.MaxSnapshotAge.TotalMinutes:F0} minutes.",
                    settings.AlertCooldownSeconds,
                    nowUtc, ct, activeKeySet);
            }

            return new BrokerPnLReconciliationResult(
                settings,
                AccountCount: accounts.Count,
                MissingFreshSnapshotCount: missingFreshSnapshots);
        }

        int checkedCount = 0;
        int okCount = 0;
        int warningCount = 0;
        int criticalCount = 0;
        int invalidSnapshotCount = 0;
        int currencyMismatchCount = 0;
        var criticalReasons = new List<(long AccountId, bool IsActive, string Reason)>();

        foreach (var account in accounts)
        {
            if (!snapshotsByAccount.TryGetValue(account.Id, out var snapshot))
            {
                _metrics.BrokerReconciliationOutcomes.Add(
                    1,
                    new KeyValuePair<string, object?>("metric", "equity"),
                    new KeyValuePair<string, object?>("outcome", "stale"));

                _logger.LogWarning(
                    "{Worker}: account {AccountId} ({BrokerAccountId}/{Server}) has no fresh broker PnL snapshot; maxSnapshotAge={MaxAgeMinutes:F0}m.",
                    WorkerName,
                    account.Id,
                    account.AccountId,
                    account.BrokerServer,
                    settings.MaxSnapshotAge.TotalMinutes);

                // Without fresh data we cannot re-evaluate variance — resolve any prior variance
                // alerts so they don't imply a condition we can no longer confirm.
                await ResolveAlertsAsync(
                    serviceProvider, writeContext, writeDb,
                    AllVarianceAndEquityAlertKeys(account.Id),
                    nowUtc, ct, activeKeySet);

                await UpsertAndDispatchAlertAsync(
                    serviceProvider, writeContext, writeDb,
                    BuildStaleSnapshotDedupKey(account.Id),
                    AlertType.DataQualityIssue,
                    AlertSeverity.High,
                    BuildStaleSnapshotConditionJson(account, settings, nowUtc),
                    $"Broker PnL reconciliation has no fresh broker snapshot for account {account.Id} ({account.AccountId}/{account.BrokerServer}); max snapshot age is {settings.MaxSnapshotAge.TotalMinutes:F0} minutes.",
                    settings.AlertCooldownSeconds,
                    nowUtc, ct, activeKeySet);
                continue;
            }

            await ResolveAlertsAsync(
                serviceProvider, writeContext, writeDb,
                [BuildStaleSnapshotDedupKey(account.Id)],
                nowUtc, ct, activeKeySet);

            // Currency-mismatch gate — if the broker reports a different deposit currency,
            // comparing engine/broker figures is meaningless. Skip this account until resolved.
            // Whitespace-only currency is treated as "not reported" to avoid false mismatches
            // from malformed EA payloads.
            if (!string.IsNullOrWhiteSpace(snapshot.Currency) &&
                !string.Equals(snapshot.Currency.Trim(), account.Currency, StringComparison.OrdinalIgnoreCase))
            {
                currencyMismatchCount++;
                _metrics.BrokerReconciliationOutcomes.Add(
                    1,
                    new KeyValuePair<string, object?>("metric", "equity"),
                    new KeyValuePair<string, object?>("outcome", "currency_mismatch"));

                _logger.LogError(
                    "{Worker}: currency mismatch for account {AccountId}; engine={EngineCurrency}, broker={BrokerCurrency}. Reconciliation skipped.",
                    WorkerName,
                    account.Id,
                    account.Currency,
                    snapshot.Currency);

                await UpsertAndDispatchAlertAsync(
                    serviceProvider, writeContext, writeDb,
                    BuildCurrencyMismatchDedupKey(account.Id),
                    AlertType.DataQualityIssue,
                    AlertSeverity.High,
                    BuildCurrencyMismatchConditionJson(account, snapshot, nowUtc),
                    $"Broker PnL reconciliation cannot evaluate account {account.Id}: broker currency '{snapshot.Currency}' does not match engine currency '{account.Currency}'.",
                    settings.AlertCooldownSeconds,
                    nowUtc, ct, activeKeySet);
                continue;
            }

            await ResolveAlertsAsync(
                serviceProvider, writeContext, writeDb,
                [BuildCurrencyMismatchDedupKey(account.Id)],
                nowUtc, ct, activeKeySet);

            checkedCount++;

            if (snapshot.Equity <= 0m || snapshot.Equity < settings.MinimumBrokerEquity)
            {
                invalidSnapshotCount++;
                _metrics.BrokerReconciliationOutcomes.Add(
                    1,
                    new KeyValuePair<string, object?>("metric", "equity"),
                    new KeyValuePair<string, object?>("outcome", "invalid"));
                // Balance reconciliation is skipped when equity is invalid — emit a counterpart
                // outcome so dashboards don't silently lose per-metric coverage for this account.
                _metrics.BrokerReconciliationOutcomes.Add(
                    1,
                    new KeyValuePair<string, object?>("metric", "balance"),
                    new KeyValuePair<string, object?>("outcome", "invalid"));

                // Without a usable equity we cannot re-evaluate variance — resolve any prior
                // variance alerts (both metrics) so they don't imply a condition we no longer check.
                await ResolveAlertsAsync(
                    serviceProvider, writeContext, writeDb,
                    [
                        BuildMetricVarianceDedupKey(account.Id, "equity", "warning"),
                        BuildMetricVarianceDedupKey(account.Id, "equity", "critical"),
                        BuildBalanceVarianceDedupKey(account.Id, "warning"),
                        BuildBalanceVarianceDedupKey(account.Id, "critical")
                    ],
                    nowUtc, ct, activeKeySet);

                await UpsertAndDispatchAlertAsync(
                    serviceProvider, writeContext, writeDb,
                    BuildInvalidBrokerEquityDedupKey(account.Id),
                    AlertType.DataQualityIssue,
                    AlertSeverity.High,
                    BuildInvalidBrokerEquityConditionJson(account, snapshot, settings, nowUtc),
                    $"Broker PnL reconciliation cannot evaluate account {account.Id}: broker equity {snapshot.Equity:F2} {account.Currency} is below the minimum valid equity {settings.MinimumBrokerEquity:F2}.",
                    settings.AlertCooldownSeconds,
                    nowUtc, ct, activeKeySet);

                _logger.LogWarning(
                    "{Worker}: invalid broker equity for account {AccountId}; snapshot={SnapshotId}, instance={InstanceId}, brokerEquity={BrokerEquity:F2}, minimum={Minimum:F2}.",
                    WorkerName,
                    account.Id,
                    snapshot.Id,
                    snapshot.InstanceId,
                    snapshot.Equity,
                    settings.MinimumBrokerEquity);
                continue;
            }

            await ResolveAlertsAsync(
                serviceProvider, writeContext, writeDb,
                [BuildInvalidBrokerEquityDedupKey(account.Id)],
                nowUtc, ct, activeKeySet);

            var age = nowUtc - NormalizeUtc(snapshot.ReportedAt);
            _metrics.BrokerReconciliationSnapshotAgeSeconds.Record(
                age.TotalSeconds,
                new KeyValuePair<string, object?>("account_id", account.Id));

            var equityOutcome = await ReconcileMetricAsync(
                serviceProvider, writeContext, writeDb,
                account, snapshot, settings,
                engineValue: account.Equity,
                brokerValue: snapshot.Equity,
                metric: "equity",
                age: age,
                nowUtc: nowUtc,
                activeKeyTracker: activeKeySet,
                ct: ct);

            ReconcileMetricResult? balanceOutcome = null;
            if (settings.ReconcileBalance && snapshot.Balance > 0m)
            {
                balanceOutcome = await ReconcileMetricAsync(
                    serviceProvider, writeContext, writeDb,
                    account, snapshot, settings,
                    engineValue: account.Balance,
                    brokerValue: snapshot.Balance,
                    metric: "balance",
                    age: age,
                    nowUtc: nowUtc,
                    activeKeyTracker: activeKeySet,
                    ct: ct);
            }
            else
            {
                // Balance reconciliation disabled or broker reports zero balance — clear any prior balance alerts.
                await ResolveAlertsAsync(
                    serviceProvider, writeContext, writeDb,
                    [
                        BuildBalanceVarianceDedupKey(account.Id, "warning"),
                        BuildBalanceVarianceDedupKey(account.Id, "critical")
                    ],
                    nowUtc, ct, activeKeySet);
            }

            ReconcileSeverity maxSeverity = equityOutcome.Severity;
            if (balanceOutcome is not null && balanceOutcome.Severity > maxSeverity)
                maxSeverity = balanceOutcome.Severity;

            switch (maxSeverity)
            {
                case ReconcileSeverity.Critical:
                    criticalCount++;
                    var reason = equityOutcome.Severity == ReconcileSeverity.Critical
                        ? equityOutcome.Reason!
                        : balanceOutcome!.Reason!;
                    criticalReasons.Add((account.Id, account.IsActive, reason));
                    break;
                case ReconcileSeverity.Warning:
                    warningCount++;
                    break;
                default:
                    okCount++;
                    break;
            }
        }

        // Deferred global kill switch — activate once if enough active critical accounts tripped.
        bool killSwitchActivated = false;
        int activeCriticalCount = criticalReasons.Count(r => r.IsActive);
        if (settings.AutoKillSwitchOnCritical &&
            activeCriticalCount >= Math.Max(1, settings.RequiredCriticalAccountsForGlobalKill))
        {
            string aggregateReason = criticalReasons.Count == 1
                ? criticalReasons[0].Reason
                : $"Broker PnL reconciliation: {activeCriticalCount} active account(s) exceeded critical variance threshold. First: {criticalReasons[0].Reason}";

            killSwitchActivated = await TryActivateGlobalKillSwitchAsync(aggregateReason, ct);
        }

        return new BrokerPnLReconciliationResult(
            settings,
            AccountCount: accounts.Count,
            FreshSnapshotCount: latestSnapshots.Count,
            CheckedCount: checkedCount,
            OkCount: okCount,
            WarningCount: warningCount,
            CriticalCount: criticalCount,
            InvalidSnapshotCount: invalidSnapshotCount,
            CurrencyMismatchCount: currencyMismatchCount,
            MissingFreshSnapshotCount: missingFreshSnapshots,
            KillSwitchActivated: killSwitchActivated);
    }

    private async Task<ReconcileMetricResult> ReconcileMetricAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext writeDb,
        TradingAccountProjection account,
        BrokerAccountSnapshotProjection snapshot,
        BrokerPnLReconciliationSettings settings,
        decimal engineValue,
        decimal brokerValue,
        string metric,
        TimeSpan age,
        DateTime nowUtc,
        HashSet<string> activeKeyTracker,
        CancellationToken ct)
    {
        decimal signedDelta = engineValue - brokerValue;
        decimal varianceDecimal = Math.Abs(signedDelta) / brokerValue;
        double variance = (double)varianceDecimal;
        string direction = signedDelta == 0m ? "exact" : signedDelta > 0m ? "over" : "under";

        _metrics.BrokerReconciliationVariance.Record(
            variance,
            new KeyValuePair<string, object?>("metric", metric),
            new KeyValuePair<string, object?>("account_id", account.Id),
            new KeyValuePair<string, object?>("direction", direction));

        string warningKey = BuildMetricVarianceDedupKey(account.Id, metric, "warning");
        string criticalKey = BuildMetricVarianceDedupKey(account.Id, metric, "critical");

        if (variance >= settings.CriticalVarianceFraction)
        {
            _metrics.BrokerReconciliationOutcomes.Add(
                1,
                new KeyValuePair<string, object?>("metric", metric),
                new KeyValuePair<string, object?>("outcome", "critical"));

            await ResolveAlertsAsync(serviceProvider, writeContext, writeDb, [warningKey], nowUtc, ct, activeKeyTracker);

            string reason =
                $"Broker PnL {metric} variance {variance:P2} for account {account.Id} " +
                $"exceeds critical threshold {settings.CriticalVarianceFraction:P2}; " +
                $"engine={engineValue:F2} {account.Currency}, broker={brokerValue:F2} {account.Currency}.";

            await UpsertAndDispatchAlertAsync(
                serviceProvider, writeContext, writeDb,
                criticalKey,
                AlertType.BrokerReconciliation,
                AlertSeverity.Critical,
                BuildVarianceConditionJson(account, snapshot, settings, variance, metric, "critical", nowUtc),
                reason,
                settings.AlertCooldownSeconds,
                nowUtc, ct, activeKeyTracker);

            _logger.LogCritical(
                "{Worker}: PnL reconciliation CRITICAL for account {AccountId} ({Metric}); variance={Variance:P2} threshold={Threshold:P2}, engine={EngineValue:F2} {Currency}, broker={BrokerValue:F2} {Currency}, snapshot={SnapshotId}, instance={InstanceId}, snapshotAge={SnapshotAgeMinutes:F1}m.",
                WorkerName, account.Id, metric, variance, settings.CriticalVarianceFraction,
                engineValue, account.Currency, brokerValue, account.Currency,
                snapshot.Id, snapshot.InstanceId, age.TotalMinutes);

            return new ReconcileMetricResult(ReconcileSeverity.Critical, variance, reason);
        }

        if (variance >= settings.WarningVarianceFraction)
        {
            _metrics.BrokerReconciliationOutcomes.Add(
                1,
                new KeyValuePair<string, object?>("metric", metric),
                new KeyValuePair<string, object?>("outcome", "warning"));

            await ResolveAlertsAsync(serviceProvider, writeContext, writeDb, [criticalKey], nowUtc, ct, activeKeyTracker);

            await UpsertAndDispatchAlertAsync(
                serviceProvider, writeContext, writeDb,
                warningKey,
                AlertType.BrokerReconciliation,
                AlertSeverity.High,
                BuildVarianceConditionJson(account, snapshot, settings, variance, metric, "warning", nowUtc),
                $"Broker PnL {metric} reconciliation warning for account {account.Id}: variance {variance:P2} exceeds warning threshold {settings.WarningVarianceFraction:P2}. Engine={engineValue:F2} {account.Currency}, broker={brokerValue:F2} {account.Currency}.",
                settings.AlertCooldownSeconds,
                nowUtc, ct, activeKeyTracker);

            _logger.LogWarning(
                "{Worker}: PnL reconciliation warning for account {AccountId} ({Metric}); variance={Variance:P2} threshold={Threshold:P2}, engine={EngineValue:F2} {Currency}, broker={BrokerValue:F2} {Currency}, snapshot={SnapshotId}, instance={InstanceId}, snapshotAge={SnapshotAgeMinutes:F1}m.",
                WorkerName, account.Id, metric, variance, settings.WarningVarianceFraction,
                engineValue, account.Currency, brokerValue, account.Currency,
                snapshot.Id, snapshot.InstanceId, age.TotalMinutes);

            return new ReconcileMetricResult(ReconcileSeverity.Warning, variance, null);
        }

        _metrics.BrokerReconciliationOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("metric", metric),
            new KeyValuePair<string, object?>("outcome", "ok"));

        await ResolveAlertsAsync(
            serviceProvider, writeContext, writeDb,
            [warningKey, criticalKey], nowUtc, ct, activeKeyTracker);

        _logger.LogDebug(
            "{Worker}: PnL reconciliation OK for account {AccountId} ({Metric}); variance={Variance:P4}, engine={EngineValue:F2} {Currency}, broker={BrokerValue:F2} {Currency}, snapshotAge={SnapshotAgeMinutes:F1}m.",
            WorkerName, account.Id, metric, variance,
            engineValue, account.Currency, brokerValue, account.Currency,
            age.TotalMinutes);

        return new ReconcileMetricResult(ReconcileSeverity.Ok, variance, null);
    }

    private enum ReconcileSeverity { Ok, Warning, Critical }

    private sealed record ReconcileMetricResult(ReconcileSeverity Severity, double Variance, string? Reason);

    private async Task<bool> TryActivateGlobalKillSwitchAsync(string reason, CancellationToken ct)
    {
        try
        {
            if (await _killSwitch.IsGlobalKilledAsync(ct))
                return false;

            await _killSwitch.SetGlobalAsync(true, reason, ct);
            _metrics.KillSwitchTriggered.Add(
                1,
                new KeyValuePair<string, object?>("scope", "global"),
                new KeyValuePair<string, object?>("site", "broker_pnl_reconciliation"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "{Worker}: failed to activate global kill switch after critical PnL variance.",
                WorkerName);
            _metrics.WorkerErrors.Add(
                1,
                new KeyValuePair<string, object?>("worker", WorkerName),
                new KeyValuePair<string, object?>("reason", "kill_switch_activation_failed"));
            return false;
        }
    }

    private async Task UpsertAndDispatchAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext writeDb,
        string deduplicationKey,
        AlertType alertType,
        AlertSeverity severity,
        string conditionJson,
        string message,
        int cooldownSeconds,
        DateTime nowUtc,
        CancellationToken ct,
        HashSet<string>? activeKeyTracker = null)
    {
        activeKeyTracker?.Add(deduplicationKey);
        var alert = await writeDb.Set<Alert>()
            .FirstOrDefaultAsync(a => !a.IsDeleted && a.DeduplicationKey == deduplicationKey && a.IsActive, ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = alertType,
                DeduplicationKey = deduplicationKey,
                IsActive = true,
            };

            writeDb.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = alertType;
        }

        alert.ConditionJson = Truncate(conditionJson, 1000);
        alert.Severity = severity;
        alert.CooldownSeconds = cooldownSeconds;
        alert.AutoResolvedAt = null;

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLikelyAlertDeduplicationRace(serviceProvider, ex))
        {
            DetachIfAdded(writeDb, alert);

            alert = await writeDb.Set<Alert>()
                .FirstAsync(a => !a.IsDeleted && a.DeduplicationKey == deduplicationKey && a.IsActive, ct);
            alert.ConditionJson = Truncate(conditionJson, 1000);
            alert.Severity = severity;
            alert.CooldownSeconds = cooldownSeconds;
            alert.AutoResolvedAt = null;
            await writeContext.SaveChangesAsync(ct);
        }

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();

        if (alert.LastTriggeredAt.HasValue &&
            nowUtc - NormalizeUtc(alert.LastTriggeredAt.Value) < TimeSpan.FromSeconds(cooldownSeconds))
        {
            return;
        }

        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: alert dispatch failed for dedup key {DeduplicationKey}.",
                WorkerName,
                deduplicationKey);
        }
    }

    private async Task ResolveAlertsAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext writeDb,
        IReadOnlyCollection<string> deduplicationKeys,
        DateTime nowUtc,
        CancellationToken ct,
        HashSet<string>? activeKeyTracker = null)
    {
        // Client-side filter: if the cycle tracker tells us a key is not currently active,
        // skip the DB round-trip entirely. This turns the happy-path from ~5N queries/account
        // into a handful of queries/cycle where actual state transitions occur.
        IReadOnlyCollection<string> effectiveKeys = activeKeyTracker is null
            ? deduplicationKeys
            : deduplicationKeys.Where(activeKeyTracker.Contains).ToList();

        if (effectiveKeys.Count == 0)
            return;

        var alerts = await writeDb.Set<Alert>()
            .Where(a => !a.IsDeleted
                     && a.IsActive
                     && a.DeduplicationKey != null
                     && effectiveKeys.Contains(a.DeduplicationKey))
            .ToListAsync(ct);

        if (alerts.Count == 0)
        {
            // DB says none of these are active — keep the tracker in sync.
            if (activeKeyTracker is not null)
                foreach (var key in effectiveKeys)
                    activeKeyTracker.Remove(key);
            return;
        }

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        foreach (var alert in alerts)
        {
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
                        "{Worker}: alert auto-resolve dispatch failed for dedup key {DeduplicationKey}.",
                        WorkerName,
                        alert.DeduplicationKey);
                }
            }

            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;

            if (activeKeyTracker is not null && alert.DeduplicationKey is not null)
                activeKeyTracker.Remove(alert.DeduplicationKey);
        }

        await writeContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Dedup keys representing all variance/bookkeeping alerts this worker owns for a given
    /// trading account. Used when data becomes unavailable — we resolve all of them so the
    /// alert store reflects "we don't know" rather than a stale prior evaluation.
    /// </summary>
    private static string[] AllVarianceAndEquityAlertKeys(long accountId)
        =>
        [
            BuildMetricVarianceDedupKey(accountId, "equity", "warning"),
            BuildMetricVarianceDedupKey(accountId, "equity", "critical"),
            BuildBalanceVarianceDedupKey(accountId, "warning"),
            BuildBalanceVarianceDedupKey(accountId, "critical"),
            BuildCurrencyMismatchDedupKey(accountId),
            BuildInvalidBrokerEquityDedupKey(accountId),
        ];

    private async Task<BrokerPnLReconciliationSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            EnabledConfigKey,
            IntervalMinutesConfigKey,
            InitialDelaySecondsConfigKey,
            MaxSnapshotAgeMinutesConfigKey,
            FutureSnapshotToleranceMinutesConfigKey,
            WarningVariancePctConfigKey,
            CriticalVariancePctConfigKey,
            LegacyWarningVarianceConfigKey,
            LegacyCriticalVarianceConfigKey,
            MinimumBrokerEquityConfigKey,
            ActiveAccountsOnlyConfigKey,
            AutoKillSwitchOnCriticalConfigKey,
            RequiredCriticalAccountsForGlobalKillConfigKey,
            ReconcileBalanceConfigKey,
            AlertCooldownSecondsConfigKey,
            LockTimeoutSecondsConfigKey
        ];

        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => keys.Contains(c.Key))
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        var values = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        // Detect legacy-fraction-key usage and warn the operator before it bites them.
        // The legacy keys accept a fraction (0.005) OR a percent (0.5) depending on magnitude;
        // the new Pct keys always interpret the value as a percentage.
        // Log once per process per key to avoid log spam on hourly cycles until migration happens.
        if (!values.ContainsKey(WarningVariancePctConfigKey) && values.ContainsKey(LegacyWarningVarianceConfigKey)
            && !_legacyWarningVarianceKeyLogged)
        {
            _logger.LogWarning(
                "{Worker}: using legacy config key {LegacyKey}; migrate to {NewKey} (value is ALWAYS interpreted as a percentage, e.g. '0.5' means 0.5%). This warning is logged once per process.",
                WorkerName,
                LegacyWarningVarianceConfigKey,
                WarningVariancePctConfigKey);
            _legacyWarningVarianceKeyLogged = true;
        }

        if (!values.ContainsKey(CriticalVariancePctConfigKey) && values.ContainsKey(LegacyCriticalVarianceConfigKey)
            && !_legacyCriticalVarianceKeyLogged)
        {
            _logger.LogWarning(
                "{Worker}: using legacy config key {LegacyKey}; migrate to {NewKey} (value is ALWAYS interpreted as a percentage, e.g. '2' means 2%). This warning is logged once per process.",
                WorkerName,
                LegacyCriticalVarianceConfigKey,
                CriticalVariancePctConfigKey);
            _legacyCriticalVarianceKeyLogged = true;
        }

        var warningVariance = ReadVarianceFraction(
            values,
            WarningVariancePctConfigKey,
            LegacyWarningVarianceConfigKey,
            DefaultWarningVarianceFraction);
        var criticalVariance = ReadVarianceFraction(
            values,
            CriticalVariancePctConfigKey,
            LegacyCriticalVarianceConfigKey,
            DefaultCriticalVarianceFraction);

        if (criticalVariance <= warningVariance)
        {
            _logger.LogWarning(
                "{Worker}: configured critical threshold {Critical:P4} is not greater than warning threshold {Warning:P4}; using default critical threshold {Default:P2}.",
                WorkerName,
                criticalVariance,
                warningVariance,
                DefaultCriticalVarianceFraction);
            criticalVariance = Math.Max(DefaultCriticalVarianceFraction, warningVariance * 4);
        }

        if (criticalVariance <= warningVariance)
            criticalVariance = warningVariance + 0.001;

        return new BrokerPnLReconciliationSettings(
            Enabled: ReadBool(values, EnabledConfigKey, true),
            InitialDelay: TimeSpan.FromSeconds(ReadInt(values, InitialDelaySecondsConfigKey, (int)DefaultInitialDelay.TotalSeconds, min: 0, max: 3600)),
            Interval: TimeSpan.FromMinutes(ReadInt(values, IntervalMinutesConfigKey, (int)DefaultInterval.TotalMinutes, min: 1, max: 1440)),
            MaxSnapshotAge: TimeSpan.FromMinutes(ReadInt(values, MaxSnapshotAgeMinutesConfigKey, (int)DefaultMaxSnapshotAge.TotalMinutes, min: 1, max: 1440)),
            FutureSnapshotTolerance: TimeSpan.FromMinutes(ReadInt(values, FutureSnapshotToleranceMinutesConfigKey, (int)DefaultFutureSnapshotTolerance.TotalMinutes, min: 0, max: 60)),
            LockTimeout: TimeSpan.FromSeconds(ReadInt(values, LockTimeoutSecondsConfigKey, (int)DefaultLockTimeout.TotalSeconds, min: 0, max: 120)),
            WarningVarianceFraction: warningVariance,
            CriticalVarianceFraction: criticalVariance,
            MinimumBrokerEquity: ReadDecimal(values, MinimumBrokerEquityConfigKey, DefaultMinimumBrokerEquity, min: 0m),
            ActiveAccountsOnly: ReadBool(values, ActiveAccountsOnlyConfigKey, true),
            AutoKillSwitchOnCritical: ReadBool(values, AutoKillSwitchOnCriticalConfigKey, true),
            RequiredCriticalAccountsForGlobalKill: ReadInt(values, RequiredCriticalAccountsForGlobalKillConfigKey, 1, min: 1, max: 1000),
            ReconcileBalance: ReadBool(values, ReconcileBalanceConfigKey, true),
            AlertCooldownSeconds: ReadInt(values, AlertCooldownSecondsConfigKey, DefaultAlertCooldownSeconds, min: 60, max: 86_400));
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        if (!values.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return parsed < min || parsed > max ? defaultValue : parsed;
    }

    private static decimal ReadDecimal(
        IReadOnlyDictionary<string, string> values,
        string key,
        decimal defaultValue,
        decimal min)
    {
        if (!values.TryGetValue(key, out var raw) ||
            !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return parsed < min ? defaultValue : parsed;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsed))
            return parsed;

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }

    private static double ReadVarianceFraction(
        IReadOnlyDictionary<string, string> values,
        string percentKey,
        string legacyFractionKey,
        double defaultValue)
    {
        if (values.TryGetValue(percentKey, out var rawPercent) &&
            TryReadPositiveDouble(rawPercent, out var percent) &&
            percent <= 100d)
        {
            return percent / 100d;
        }

        if (values.TryGetValue(legacyFractionKey, out var rawLegacy) &&
            TryReadPositiveDouble(rawLegacy, out var legacy))
        {
            return legacy <= 1d ? legacy : legacy / 100d;
        }

        return defaultValue;
    }

    private static bool TryReadPositiveDouble(string? raw, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value) &&
            value > 0d)
        {
            return true;
        }

        value = 0d;
        return false;
    }

    private static string BuildVarianceConditionJson(
        TradingAccountProjection account,
        BrokerAccountSnapshotProjection snapshot,
        BrokerPnLReconciliationSettings settings,
        double variance,
        string metric,
        string severity,
        DateTime observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            reason = "BrokerPnLVariance",
            metric,
            severity,
            tradingAccountId = account.Id,
            brokerAccountId = account.AccountId,
            brokerServer = account.BrokerServer,
            currency = account.Currency,
            engineEquity = account.Equity,
            brokerEquity = snapshot.Equity,
            engineBalance = account.Balance,
            brokerBalance = snapshot.Balance,
            variance,
            warningThreshold = settings.WarningVarianceFraction,
            criticalThreshold = settings.CriticalVarianceFraction,
            snapshotId = snapshot.Id,
            instanceId = snapshot.InstanceId,
            snapshotReportedAt = NormalizeUtc(snapshot.ReportedAt),
            observedAtUtc
        });

    private static string BuildInvalidBrokerEquityConditionJson(
        TradingAccountProjection account,
        BrokerAccountSnapshotProjection snapshot,
        BrokerPnLReconciliationSettings settings,
        DateTime observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            reason = "InvalidBrokerEquity",
            tradingAccountId = account.Id,
            brokerAccountId = account.AccountId,
            brokerServer = account.BrokerServer,
            currency = account.Currency,
            brokerEquity = snapshot.Equity,
            minimumBrokerEquity = settings.MinimumBrokerEquity,
            snapshotId = snapshot.Id,
            instanceId = snapshot.InstanceId,
            snapshotReportedAt = NormalizeUtc(snapshot.ReportedAt),
            observedAtUtc
        });

    private static string BuildStaleSnapshotConditionJson(
        TradingAccountProjection account,
        BrokerPnLReconciliationSettings settings,
        DateTime observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            reason = "MissingFreshBrokerPnLSnapshot",
            tradingAccountId = account.Id,
            brokerAccountId = account.AccountId,
            brokerServer = account.BrokerServer,
            currency = account.Currency,
            maxSnapshotAgeMinutes = settings.MaxSnapshotAge.TotalMinutes,
            observedAtUtc
        });

    private static string BuildCurrencyMismatchConditionJson(
        TradingAccountProjection account,
        BrokerAccountSnapshotProjection snapshot,
        DateTime observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            reason = "BrokerCurrencyMismatch",
            tradingAccountId = account.Id,
            brokerAccountId = account.AccountId,
            brokerServer = account.BrokerServer,
            engineCurrency = account.Currency,
            brokerCurrency = snapshot.Currency,
            snapshotId = snapshot.Id,
            instanceId = snapshot.InstanceId,
            snapshotReportedAt = NormalizeUtc(snapshot.ReportedAt),
            observedAtUtc
        });

    // Equity dedup keys keep the pre-existing format (BrokerPnL:Variance:{accountId}:{severity})
    // so active alerts survive the upgrade without orphaning. Balance keys are new and namespaced.
    private static string BuildMetricVarianceDedupKey(long accountId, string metric, string severity)
        => metric == "equity"
            ? $"BrokerPnL:Variance:{accountId}:{severity}"
            : $"BrokerPnL:Variance:{accountId}:{metric}:{severity}";

    private static string BuildBalanceVarianceDedupKey(long accountId, string severity)
        => BuildMetricVarianceDedupKey(accountId, "balance", severity);

    private static string BuildInvalidBrokerEquityDedupKey(long accountId)
        => $"BrokerPnL:InvalidBrokerEquity:{accountId}";

    private static string BuildStaleSnapshotDedupKey(long accountId)
        => $"BrokerPnL:StaleSnapshot:{accountId}";

    private static string BuildCurrencyMismatchDedupKey(long accountId)
        => $"BrokerPnL:CurrencyMismatch:{accountId}";

    private static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

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

    private static void DetachIfAdded(DbContext db, Alert alert)
    {
        var entry = db.Entry(alert);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }

    private sealed record TradingAccountProjection(
        long Id,
        string AccountId,
        string BrokerServer,
        string Currency,
        decimal Equity,
        decimal Balance,
        bool IsActive);

    private sealed record BrokerAccountSnapshotProjection(
        long Id,
        long TradingAccountId,
        string InstanceId,
        decimal Balance,
        decimal Equity,
        string Currency,
        DateTime ReportedAt);

    internal sealed record BrokerPnLReconciliationSettings(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan Interval,
        TimeSpan MaxSnapshotAge,
        TimeSpan FutureSnapshotTolerance,
        TimeSpan LockTimeout,
        double WarningVarianceFraction,
        double CriticalVarianceFraction,
        decimal MinimumBrokerEquity,
        bool ActiveAccountsOnly,
        bool AutoKillSwitchOnCritical,
        int RequiredCriticalAccountsForGlobalKill,
        bool ReconcileBalance,
        int AlertCooldownSeconds)
    {
        public static BrokerPnLReconciliationSettings Default { get; } = new(
            Enabled: true,
            InitialDelay: DefaultInitialDelay,
            Interval: DefaultInterval,
            MaxSnapshotAge: DefaultMaxSnapshotAge,
            FutureSnapshotTolerance: DefaultFutureSnapshotTolerance,
            LockTimeout: DefaultLockTimeout,
            WarningVarianceFraction: DefaultWarningVarianceFraction,
            CriticalVarianceFraction: DefaultCriticalVarianceFraction,
            MinimumBrokerEquity: DefaultMinimumBrokerEquity,
            ActiveAccountsOnly: true,
            AutoKillSwitchOnCritical: true,
            RequiredCriticalAccountsForGlobalKill: 1,
            ReconcileBalance: true,
            AlertCooldownSeconds: DefaultAlertCooldownSeconds);
    }

    internal sealed record BrokerPnLReconciliationResult(
        BrokerPnLReconciliationSettings Settings,
        int AccountCount = 0,
        int FreshSnapshotCount = 0,
        int CheckedCount = 0,
        int OkCount = 0,
        int WarningCount = 0,
        int CriticalCount = 0,
        int InvalidSnapshotCount = 0,
        int CurrencyMismatchCount = 0,
        int MissingFreshSnapshotCount = 0,
        bool KillSwitchActivated = false,
        string? SkippedReason = null)
    {
        public static BrokerPnLReconciliationResult Skipped(
            BrokerPnLReconciliationSettings settings,
            string reason)
            => new(settings, SkippedReason: reason);
    }
}
