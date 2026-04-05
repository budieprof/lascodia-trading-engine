using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Runs every hour to compute intraday performance attribution snapshots.
/// Reads open and recently closed positions, computes running P&amp;L per
/// strategy/symbol group, and writes AccountPerformanceAttribution records
/// with hourly granularity.
/// </summary>
public class IntradayAttributionWorker : BackgroundService
{
    private readonly ILogger<IntradayAttributionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IntradayAttributionOptions _options;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    private int _consecutiveFailures;

    public IntradayAttributionWorker(
        ILogger<IntradayAttributionWorker> logger,
        IServiceScopeFactory scopeFactory,
        IntradayAttributionOptions options)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IntradayAttributionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled)
                {
                    await ComputeHourlyAttributionAsync(stoppingToken);
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
                _logger.LogError(ex, "IntradayAttributionWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task ComputeHourlyAttributionAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var now = DateTime.UtcNow;
        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var dayStart  = now.Date;

        var accounts = await readDb
            .Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);

        var processedCount = 0;

        foreach (var account in accounts)
        {
            var existing = await readDb
                .Set<AccountPerformanceAttribution>()
                .AnyAsync(a => a.TradingAccountId == account.Id
                            && a.AttributionDate == hourStart
                            && !a.IsDeleted, ct);

            if (existing) continue;

            // ── Determine SOD equity from the most reliable source ────────────
            // 1st: Previous hourly attribution record from today
            // 2nd: DrawdownSnapshot recorded near SOD
            // 3rd: Fallback to current equity minus today's P&L (approximation)
            decimal startOfDayEquity = await DetermineStartOfDayEquityAsync(
                readDb, account.Id, dayStart, ct);

            // ── Closed positions scoped to this account via Order link ────────
            var accountOrderIds = await readDb.Set<Order>()
                .Where(o => o.TradingAccountId == account.Id && !o.IsDeleted)
                .Select(o => o.Id)
                .ToListAsync(ct);

            var accountOrderIdSet = new HashSet<long>(accountOrderIds);

            var closedPositions = await readDb
                .Set<Position>()
                .Where(p => p.Status == PositionStatus.Closed
                         && p.ClosedAt >= dayStart && p.ClosedAt <= now
                         && p.OpenOrderId != null
                         && !p.IsDeleted)
                .ToListAsync(ct);

            // Filter to positions belonging to this account
            closedPositions = closedPositions
                .Where(p => p.OpenOrderId.HasValue && accountOrderIdSet.Contains(p.OpenOrderId.Value))
                .ToList();

            // ── Open positions for unrealized P&L ────────────────────────────
            var openPositions = await readDb
                .Set<Position>()
                .Where(p => p.Status == PositionStatus.Open
                         && p.OpenOrderId != null
                         && !p.IsDeleted)
                .ToListAsync(ct);

            openPositions = openPositions
                .Where(p => p.OpenOrderId.HasValue && accountOrderIdSet.Contains(p.OpenOrderId.Value))
                .ToList();

            decimal unrealizedPnlChange = openPositions.Sum(p => p.UnrealizedPnL);

            // ── TCA records for today scoped to account orders ───────────────
            var tcaRecords = await readDb
                .Set<TransactionCostAnalysis>()
                .Where(t => t.AnalyzedAt >= dayStart && t.AnalyzedAt <= now && !t.IsDeleted)
                .ToListAsync(ct);

            tcaRecords = tcaRecords
                .Where(t => accountOrderIdSet.Contains(t.OrderId))
                .ToList();

            decimal realizedPnl = closedPositions.Sum(p => p.RealizedPnL);
            decimal executionCosts = tcaRecords.Sum(t => t.TotalCost);
            int tradeCount = closedPositions.Count;
            int winCount = closedPositions.Count(p => p.RealizedPnL > 0);
            decimal winRate = tradeCount > 0 ? (decimal)winCount / tradeCount : 0;

            // Use SOD equity; fall back to approximation only if not found
            if (startOfDayEquity == 0m)
                startOfDayEquity = account.Equity - realizedPnl - unrealizedPnlChange;

            decimal totalReturn = realizedPnl + unrealizedPnlChange;
            decimal dailyReturnPct = startOfDayEquity > 0
                ? totalReturn / startOfDayEquity * 100m
                : 0;

            // ── Strategy attribution via Order.StrategyId ────────────────────
            var closedPositionOrderIds = closedPositions
                .Where(p => p.OpenOrderId.HasValue)
                .Select(p => p.OpenOrderId!.Value)
                .Distinct()
                .ToList();

            var orderStrategyMap = await readDb.Set<Order>()
                .Where(o => closedPositionOrderIds.Contains(o.Id))
                .Select(o => new { o.Id, o.StrategyId })
                .ToDictionaryAsync(o => o.Id, o => o.StrategyId, ct);

            var strategyNames = await readDb.Set<Strategy>()
                .Where(s => orderStrategyMap.Values.Distinct().Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

            var strategyGroups = closedPositions
                .Where(p => p.OpenOrderId.HasValue && orderStrategyMap.ContainsKey(p.OpenOrderId.Value))
                .GroupBy(p => orderStrategyMap[p.OpenOrderId!.Value])
                .Select(g => new
                {
                    strategyId   = g.Key,
                    strategyName = strategyNames.GetValueOrDefault(g.Key, $"Strategy-{g.Key}"),
                    pnl          = g.Sum(p => p.RealizedPnL),
                    trades       = g.Count(),
                    winRate      = g.Count() > 0 ? (decimal)g.Count(p => p.RealizedPnL > 0) / g.Count() : 0m
                })
                .ToList();

            // ── Symbol attribution ───────────────────────────────────────────
            var symbolGroups = closedPositions
                .GroupBy(p => p.Symbol)
                .Select(g => new
                {
                    symbol = g.Key,
                    pnl    = g.Sum(p => p.RealizedPnL),
                    trades = g.Count(),
                    winRate = g.Count() > 0 ? (decimal)g.Count(p => p.RealizedPnL > 0) / g.Count() : 0m
                })
                .ToList();

            decimal grossAlphaPct = dailyReturnPct;
            decimal executionCostPct = startOfDayEquity > 0
                ? executionCosts / startOfDayEquity * 100m
                : 0;
            decimal netAlphaPct = grossAlphaPct - executionCostPct;

            var attribution = new AccountPerformanceAttribution
            {
                TradingAccountId        = account.Id,
                AttributionDate         = hourStart,
                StartOfDayEquity        = startOfDayEquity,
                EndOfDayEquity          = account.Equity,
                RealizedPnl             = realizedPnl,
                UnrealizedPnlChange     = unrealizedPnlChange,
                DailyReturnPct          = dailyReturnPct,
                StrategyAttributionJson = System.Text.Json.JsonSerializer.Serialize(strategyGroups),
                SymbolAttributionJson   = System.Text.Json.JsonSerializer.Serialize(symbolGroups),
                ExecutionCosts          = executionCosts,
                TradeCount              = tradeCount,
                WinRate                 = winRate,
                GrossAlphaPct           = grossAlphaPct,
                ExecutionCostPct        = executionCostPct,
                NetAlphaPct             = netAlphaPct
            };

            await writeDb.Set<AccountPerformanceAttribution>().AddAsync(attribution, ct);
            processedCount++;
        }

        if (processedCount > 0)
        {
            await writeDb.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "IntradayAttributionWorker: computed hourly attribution for {Count} accounts at {Hour}",
            processedCount, hourStart);
    }

    /// <summary>
    /// Determines the start-of-day equity for an account from the most reliable
    /// available source: previous attribution record, then DrawdownSnapshot.
    /// Returns 0 if no source is found (caller falls back to approximation).
    /// </summary>
    private static async Task<decimal> DetermineStartOfDayEquityAsync(
        Microsoft.EntityFrameworkCore.DbContext readDb,
        long accountId,
        DateTime dayStart,
        CancellationToken ct)
    {
        // 1st: Use the first attribution record of the day (which captured the opening state)
        var firstAttribution = await readDb
            .Set<AccountPerformanceAttribution>()
            .Where(a => a.TradingAccountId == accountId
                     && a.AttributionDate >= dayStart
                     && !a.IsDeleted)
            .OrderBy(a => a.AttributionDate)
            .FirstOrDefaultAsync(ct);

        if (firstAttribution is not null)
            return firstAttribution.StartOfDayEquity;

        // 2nd: Use the end-of-day equity from yesterday's last attribution
        var yesterdayLast = await readDb
            .Set<AccountPerformanceAttribution>()
            .Where(a => a.TradingAccountId == accountId
                     && a.AttributionDate < dayStart
                     && !a.IsDeleted)
            .OrderByDescending(a => a.AttributionDate)
            .FirstOrDefaultAsync(ct);

        if (yesterdayLast is not null)
            return yesterdayLast.EndOfDayEquity;

        return 0m;
    }
}
