using System.Diagnostics;
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
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Maintains running hourly intraday attribution snapshots for active trading accounts.
/// Snapshots are upserted for the current hour bucket so repeated cycles refresh the
/// same row instead of freezing whatever partial state happened to be captured first.
/// </summary>
public sealed class IntradayAttributionWorker : BackgroundService
{
    internal const string WorkerName = nameof(IntradayAttributionWorker);

    private const string DistributedLockKey = "workers:intraday-attribution:cycle";

    private const int DefaultPollIntervalSeconds = 3600;
    private const int MinPollIntervalSeconds = 5;
    private const int MaxPollIntervalSeconds = 24 * 60 * 60;

    private const int MaxJsonLength = 8000;

    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(2);

    private readonly ILogger<IntradayAttributionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IntradayAttributionOptions _options;
    private readonly TradingDayOptions _tradingDayOptions;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public IntradayAttributionWorker(
        ILogger<IntradayAttributionWorker> logger,
        IServiceScopeFactory scopeFactory,
        IntradayAttributionOptions options,
        TradingDayOptions tradingDayOptions,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options;
        _tradingDayOptions = tradingDayOptions;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Maintains per-account intraday attribution snapshots with hourly granularity and strategy/symbol running P&L breakdowns.",
            TimeSpan.FromSeconds(DefaultPollIntervalSeconds));

        var currentPollInterval = TimeSpan.FromSeconds(DefaultPollIntervalSeconds);

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
                    currentPollInterval = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.AccountCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.IntradayAttributionCycleDurationMs.Record(durationMs);

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
                            "{Worker}: accounts={Accounts}, inserted={Inserted}, updated={Updated}, errors={Errors}, bucket={Bucket:o}.",
                            WorkerName,
                            result.AccountCount,
                            result.InsertedCount,
                            result.UpdatedCount,
                            result.ErrorCount,
                            result.HourBucketStartUtc);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName,
                            _consecutiveFailures);
                    }

                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "intraday_attribution_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(
                        CalculateDelay(currentPollInterval, _consecutiveFailures),
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
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<IntradayAttributionCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = LoadSettings();

        if (!settings.Enabled)
        {
            _metrics?.IntradayAttributionCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return IntradayAttributionCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.IntradayAttributionLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate intraday attribution cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
            {
                _metrics?.IntradayAttributionLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.IntradayAttributionCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return IntradayAttributionCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.IntradayAttributionLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                return await RunCycleCoreAsync(db, settings, ct);
            }
        }

        return await RunCycleCoreAsync(db, settings, ct);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return baseInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(DefaultPollIntervalSeconds)
                : baseInterval;
        }

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<IntradayAttributionCycleResult> RunCycleCoreAsync(
        DbContext db,
        IntradayAttributionSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var hourBucketStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
        var tradingDayStartUtc = TradingDayBoundaryHelper.GetTradingDayStartUtc(
            nowUtc,
            _tradingDayOptions.RolloverMinuteOfDayUtc);

        var accounts = await db.Set<TradingAccount>()
            .AsNoTracking()
            .Where(account => account.IsActive && !account.IsDeleted)
            .OrderBy(account => account.Id)
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            _metrics?.IntradayAttributionCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_accounts"));
            return IntradayAttributionCycleResult.Skipped(settings, "no_active_accounts");
        }

        int inserted = 0;
        int updated = 0;
        int errorCount = 0;

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await UpsertAccountSnapshotAsync(
                    db,
                    account,
                    hourBucketStartUtc,
                    tradingDayStartUtc,
                    nowUtc,
                    ct);

                if (outcome.Inserted)
                    inserted++;

                if (outcome.Updated)
                    updated++;
            }
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to compute intraday attribution for account {AccountId}.",
                    WorkerName,
                    account.Id);
            }
        }

        if (accounts.Count > 0)
            _metrics?.IntradayAttributionAccountsEvaluated.Add(accounts.Count);

        if (inserted > 0)
            _metrics?.IntradayAttributionSnapshotsInserted.Add(inserted);

        if (updated > 0)
            _metrics?.IntradayAttributionSnapshotsUpdated.Add(updated);

        return new IntradayAttributionCycleResult(
            settings,
            HourBucketStartUtc: hourBucketStartUtc,
            AccountCount: accounts.Count,
            InsertedCount: inserted,
            UpdatedCount: updated,
            ErrorCount: errorCount,
            SkippedReason: null);
    }

    private async Task<AccountAttributionOutcome> UpsertAccountSnapshotAsync(
        DbContext db,
        TradingAccount account,
        DateTime hourBucketStartUtc,
        DateTime tradingDayStartUtc,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var accountOrders = await db.Set<Order>()
            .AsNoTracking()
            .Where(order => order.TradingAccountId == account.Id && !order.IsDeleted)
            .Select(order => new AccountOrderInfo(order.Id, order.StrategyId))
            .ToListAsync(ct);

        var orderIds = accountOrders.Select(order => order.OrderId).ToList();
        var strategyIds = accountOrders.Select(order => order.StrategyId).Distinct().ToList();

        var strategyNames = strategyIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.Set<Strategy>()
                .AsNoTracking()
                .Where(strategy => strategyIds.Contains(strategy.Id) && !strategy.IsDeleted)
                .ToDictionaryAsync(strategy => strategy.Id, strategy => strategy.Name, ct);

        var closedPositions = orderIds.Count == 0
            ? new List<PositionSnapshot>()
            : await db.Set<Position>()
                .AsNoTracking()
                .Where(position =>
                    position.Status == PositionStatus.Closed &&
                    position.OpenOrderId.HasValue &&
                    orderIds.Contains(position.OpenOrderId.Value) &&
                    position.ClosedAt >= tradingDayStartUtc &&
                    position.ClosedAt <= nowUtc &&
                    !position.IsDeleted)
                .Select(position => new PositionSnapshot(
                    position.OpenOrderId!.Value,
                    position.Symbol,
                    position.RealizedPnL,
                    position.UnrealizedPnL))
                .ToListAsync(ct);

        var openPositions = orderIds.Count == 0
            ? new List<PositionSnapshot>()
            : await db.Set<Position>()
                .AsNoTracking()
                .Where(position =>
                    position.Status == PositionStatus.Open &&
                    position.OpenOrderId.HasValue &&
                    orderIds.Contains(position.OpenOrderId.Value) &&
                    !position.IsDeleted)
                .Select(position => new PositionSnapshot(
                    position.OpenOrderId!.Value,
                    position.Symbol,
                    position.RealizedPnL,
                    position.UnrealizedPnL))
                .ToListAsync(ct);

        var executionCosts = orderIds.Count == 0
            ? 0m
            : await db.Set<TransactionCostAnalysis>()
                .AsNoTracking()
                .Where(record =>
                    orderIds.Contains(record.OrderId) &&
                    record.AnalyzedAt >= tradingDayStartUtc &&
                    record.AnalyzedAt <= nowUtc &&
                    !record.IsDeleted)
                .SumAsync(record => record.TotalCost, ct);

        decimal closedRealizedPnl = closedPositions.Sum(position => position.RealizedPnl);
        decimal openRealizedPnl = openPositions.Sum(position => position.RealizedPnl);
        decimal realizedPnl = closedRealizedPnl + openRealizedPnl;
        decimal currentUnrealizedPnl = openPositions.Sum(position => position.UnrealizedPnl);
        decimal startOfDayUnrealizedPnl = await ResolveStartOfDayUnrealizedPnlAsync(
            db,
            account.Id,
            tradingDayStartUtc,
            ct) ?? 0m;
        decimal unrealizedPnlChange = currentUnrealizedPnl - startOfDayUnrealizedPnl;

        var baseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            db,
            account.Id,
            nowUtc,
            _tradingDayOptions,
            ct);

        decimal startOfDayEquity = baseline?.StartOfDayEquity ?? (account.Equity - realizedPnl - unrealizedPnlChange);
        decimal totalReturn = realizedPnl + unrealizedPnlChange;
        decimal dailyReturnPct = startOfDayEquity > 0m
            ? totalReturn / startOfDayEquity * 100m
            : 0m;

        int tradeCount = closedPositions.Count;
        int winCount = closedPositions.Count(position => position.RealizedPnl > 0m);
        decimal winRate = tradeCount > 0
            ? (decimal)winCount / tradeCount
            : 0m;

        var strategyGroups = BuildStrategyAttribution(
            accountOrders,
            closedPositions,
            openPositions,
            strategyNames);
        var symbolGroups = BuildSymbolAttribution(closedPositions, openPositions);

        string strategyJson = SerializeStrategyGroups(strategyGroups);
        string symbolJson = SerializeSymbolGroups(symbolGroups);

        decimal executionCostPct = startOfDayEquity > 0m
            ? executionCosts / startOfDayEquity * 100m
            : 0m;

        var existing = await db.Set<AccountPerformanceAttribution>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(attribution =>
                attribution.TradingAccountId == account.Id &&
                attribution.AttributionDate == hourBucketStartUtc &&
                attribution.Granularity == PerformanceAttributionGranularity.Hourly,
                ct);

        bool inserted = existing is null;
        var attribution = existing ?? new AccountPerformanceAttribution
        {
            TradingAccountId = account.Id,
            AttributionDate = hourBucketStartUtc,
            Granularity = PerformanceAttributionGranularity.Hourly
        };

        attribution.IsDeleted = false;
        attribution.TradingAccountId = account.Id;
        attribution.AttributionDate = hourBucketStartUtc;
        attribution.Granularity = PerformanceAttributionGranularity.Hourly;
        attribution.StartOfDayEquity = startOfDayEquity;
        attribution.EndOfDayEquity = account.Equity;
        attribution.RealizedPnl = realizedPnl;
        attribution.UnrealizedPnlChange = unrealizedPnlChange;
        attribution.DailyReturnPct = dailyReturnPct;
        attribution.StrategyAttributionJson = strategyJson;
        attribution.SymbolAttributionJson = symbolJson;
        attribution.ExecutionCosts = executionCosts;
        attribution.TradeCount = tradeCount;
        attribution.WinRate = winRate;
        attribution.GrossAlphaPct = dailyReturnPct;
        attribution.ExecutionCostPct = executionCostPct;
        attribution.NetAlphaPct = dailyReturnPct - executionCostPct;

        if (inserted)
            await db.Set<AccountPerformanceAttribution>().AddAsync(attribution, ct);

        await db.SaveChangesAsync(ct);

        return inserted
            ? new AccountAttributionOutcome(Inserted: true, Updated: false)
            : new AccountAttributionOutcome(Inserted: false, Updated: true);
    }

    private async Task<decimal?> ResolveStartOfDayUnrealizedPnlAsync(
        DbContext db,
        long accountId,
        DateTime tradingDayStartUtc,
        CancellationToken ct)
    {
        var tolerance = TimeSpan.FromMinutes(Math.Max(0, _tradingDayOptions.BrokerSnapshotBoundaryToleranceMinutes));

        if (tolerance >= TimeSpan.Zero)
        {
            var snapshotBefore = await db.Set<BrokerAccountSnapshot>()
                .AsNoTracking()
                .Where(snapshot =>
                    snapshot.TradingAccountId == accountId &&
                    snapshot.ReportedAt <= tradingDayStartUtc &&
                    !snapshot.IsDeleted)
                .OrderByDescending(snapshot => snapshot.ReportedAt)
                .FirstOrDefaultAsync(ct);

            var snapshotAfter = await db.Set<BrokerAccountSnapshot>()
                .AsNoTracking()
                .Where(snapshot =>
                    snapshot.TradingAccountId == accountId &&
                    snapshot.ReportedAt >= tradingDayStartUtc &&
                    !snapshot.IsDeleted)
                .OrderBy(snapshot => snapshot.ReportedAt)
                .FirstOrDefaultAsync(ct);

            BrokerAccountSnapshot? bestSnapshot = null;
            TimeSpan bestDistance = TimeSpan.MaxValue;

            if (snapshotBefore is not null)
            {
                var distance = tradingDayStartUtc - snapshotBefore.ReportedAt;
                if (distance <= tolerance)
                {
                    bestSnapshot = snapshotBefore;
                    bestDistance = distance;
                }
            }

            if (snapshotAfter is not null)
            {
                var distance = snapshotAfter.ReportedAt - tradingDayStartUtc;
                if (distance <= tolerance && distance < bestDistance)
                    bestSnapshot = snapshotAfter;
            }

            if (bestSnapshot is not null)
                return bestSnapshot.Equity - bestSnapshot.Balance;
        }

        var previousAttribution = await db.Set<AccountPerformanceAttribution>()
            .AsNoTracking()
            .Where(attribution =>
                attribution.TradingAccountId == accountId &&
                attribution.AttributionDate < tradingDayStartUtc &&
                !attribution.IsDeleted)
            .OrderByDescending(attribution => attribution.AttributionDate)
            .FirstOrDefaultAsync(ct);

        return previousAttribution?.UnrealizedPnlChange;
    }

    private static List<StrategyAttributionItem> BuildStrategyAttribution(
        IReadOnlyList<AccountOrderInfo> accountOrders,
        IReadOnlyList<PositionSnapshot> closedPositions,
        IReadOnlyList<PositionSnapshot> openPositions,
        IReadOnlyDictionary<long, string> strategyNames)
    {
        var orderStrategyMap = accountOrders.ToDictionary(order => order.OrderId, order => order.StrategyId);

        var closedByStrategy = closedPositions
            .Where(position => orderStrategyMap.ContainsKey(position.OrderId))
            .GroupBy(position => orderStrategyMap[position.OrderId])
            .ToDictionary(group => group.Key, group => group.ToList());

        var openByStrategy = openPositions
            .Where(position => orderStrategyMap.ContainsKey(position.OrderId))
            .GroupBy(position => orderStrategyMap[position.OrderId])
            .ToDictionary(group => group.Key, group => group.ToList());

        return closedByStrategy.Keys
            .Concat(openByStrategy.Keys)
            .Distinct()
            .Select(strategyId =>
            {
                closedByStrategy.TryGetValue(strategyId, out var closedForStrategy);
                openByStrategy.TryGetValue(strategyId, out var openForStrategy);
                closedForStrategy ??= [];
                openForStrategy ??= [];

                decimal realized = closedForStrategy.Sum(position => position.RealizedPnl)
                                   + openForStrategy.Sum(position => position.RealizedPnl);
                decimal unrealized = openForStrategy.Sum(position => position.UnrealizedPnl);
                int closedTrades = closedForStrategy.Count;
                int openPositionCount = openForStrategy.Count;
                decimal winRate = closedTrades > 0
                    ? (decimal)closedForStrategy.Count(position => position.RealizedPnl > 0m) / closedTrades
                    : 0m;

                return new StrategyAttributionItem(
                    StrategyId: strategyId,
                    StrategyName: strategyNames.GetValueOrDefault(strategyId, $"Strategy-{strategyId}"),
                    RealizedPnl: realized,
                    CurrentUnrealizedPnl: unrealized,
                    Pnl: realized + unrealized,
                    ClosedTrades: closedTrades,
                    OpenPositions: openPositionCount,
                    WinRate: winRate);
            })
            .OrderByDescending(item => Math.Abs(item.Pnl))
            .ThenBy(item => item.StrategyId)
            .ToList();
    }

    private static List<SymbolAttributionItem> BuildSymbolAttribution(
        IReadOnlyList<PositionSnapshot> closedPositions,
        IReadOnlyList<PositionSnapshot> openPositions)
    {
        var closedBySymbol = closedPositions
            .GroupBy(position => position.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var openBySymbol = openPositions
            .GroupBy(position => position.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return closedBySymbol.Keys
            .Concat(openBySymbol.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(symbol =>
            {
                closedBySymbol.TryGetValue(symbol, out var closedForSymbol);
                openBySymbol.TryGetValue(symbol, out var openForSymbol);
                closedForSymbol ??= [];
                openForSymbol ??= [];

                decimal realized = closedForSymbol.Sum(position => position.RealizedPnl)
                                   + openForSymbol.Sum(position => position.RealizedPnl);
                decimal unrealized = openForSymbol.Sum(position => position.UnrealizedPnl);
                int closedTrades = closedForSymbol.Count;
                int openPositionCount = openForSymbol.Count;
                decimal winRate = closedTrades > 0
                    ? (decimal)closedForSymbol.Count(position => position.RealizedPnl > 0m) / closedTrades
                    : 0m;

                return new SymbolAttributionItem(
                    Symbol: symbol,
                    RealizedPnl: realized,
                    CurrentUnrealizedPnl: unrealized,
                    Pnl: realized + unrealized,
                    ClosedTrades: closedTrades,
                    OpenPositions: openPositionCount,
                    WinRate: winRate);
            })
            .OrderByDescending(item => Math.Abs(item.Pnl))
            .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SerializeStrategyGroups(List<StrategyAttributionItem> groups)
        => SerializeBounded(groups, aggregateTail: AggregateStrategyTail);

    private static string SerializeSymbolGroups(List<SymbolAttributionItem> groups)
        => SerializeBounded(groups, aggregateTail: AggregateSymbolTail);

    private static string SerializeBounded<T>(
        List<T> items,
        Func<List<T>, T> aggregateTail)
    {
        if (items.Count == 0)
            return "[]";

        var current = items.ToList();
        while (current.Count > 1)
        {
            string json = JsonSerializer.Serialize(current);
            if (json.Length <= MaxJsonLength)
                return json;

            var trimmed = current.Take(current.Count - 2).ToList();
            var tail = current.Skip(current.Count - 2).ToList();
            trimmed.Add(aggregateTail(tail));
            current = trimmed;
        }

        return JsonSerializer.Serialize(current);
    }

    private static StrategyAttributionItem AggregateStrategyTail(List<StrategyAttributionItem> tail)
        => new(
            StrategyId: 0,
            StrategyName: "Other",
            RealizedPnl: tail.Sum(item => item.RealizedPnl),
            CurrentUnrealizedPnl: tail.Sum(item => item.CurrentUnrealizedPnl),
            Pnl: tail.Sum(item => item.Pnl),
            ClosedTrades: tail.Sum(item => item.ClosedTrades),
            OpenPositions: tail.Sum(item => item.OpenPositions),
            WinRate: 0m);

    private static SymbolAttributionItem AggregateSymbolTail(List<SymbolAttributionItem> tail)
        => new(
            Symbol: "OTHER",
            RealizedPnl: tail.Sum(item => item.RealizedPnl),
            CurrentUnrealizedPnl: tail.Sum(item => item.CurrentUnrealizedPnl),
            Pnl: tail.Sum(item => item.Pnl),
            ClosedTrades: tail.Sum(item => item.ClosedTrades),
            OpenPositions: tail.Sum(item => item.OpenPositions),
            WinRate: 0m);

    private IntradayAttributionSettings LoadSettings()
    {
        int configuredPollSeconds = _options.PollIntervalSeconds;
        int pollSeconds = Clamp(configuredPollSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);

        LogNormalizedSetting("IntradayAttributionOptions:PollIntervalSeconds", configuredPollSeconds, pollSeconds);

        return new IntradayAttributionSettings(
            Enabled: _options.Enabled,
            PollInterval: TimeSpan.FromSeconds(pollSeconds));
    }

    private void LogNormalizedSetting<T>(string key, T configuredValue, T effectiveValue)
        where T : IEquatable<T>
    {
        if (configuredValue.Equals(effectiveValue))
            return;

        _logger.LogDebug(
            "{Worker}: normalized config {Key} from {Configured} to {Effective}.",
            WorkerName,
            key,
            configuredValue,
            effectiveValue);
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private readonly record struct AccountOrderInfo(long OrderId, long StrategyId);

    private readonly record struct PositionSnapshot(
        long OrderId,
        string Symbol,
        decimal RealizedPnl,
        decimal UnrealizedPnl);

    private readonly record struct AccountAttributionOutcome(bool Inserted, bool Updated);

    private readonly record struct StrategyAttributionItem(
        long StrategyId,
        string StrategyName,
        decimal RealizedPnl,
        decimal CurrentUnrealizedPnl,
        decimal Pnl,
        int ClosedTrades,
        int OpenPositions,
        decimal WinRate);

    private readonly record struct SymbolAttributionItem(
        string Symbol,
        decimal RealizedPnl,
        decimal CurrentUnrealizedPnl,
        decimal Pnl,
        int ClosedTrades,
        int OpenPositions,
        decimal WinRate);
}

internal readonly record struct IntradayAttributionSettings(
    bool Enabled,
    TimeSpan PollInterval);

internal readonly record struct IntradayAttributionCycleResult(
    IntradayAttributionSettings Settings,
    DateTime HourBucketStartUtc,
    int AccountCount,
    int InsertedCount,
    int UpdatedCount,
    int ErrorCount,
    string? SkippedReason)
{
    public static IntradayAttributionCycleResult Skipped(
        IntradayAttributionSettings settings,
        string reason)
        => new(
            settings,
            HourBucketStartUtc: default,
            AccountCount: 0,
            InsertedCount: 0,
            UpdatedCount: 0,
            ErrorCount: 0,
            SkippedReason: reason);
}
