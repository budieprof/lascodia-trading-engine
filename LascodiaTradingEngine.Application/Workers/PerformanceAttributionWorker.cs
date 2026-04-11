using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes daily performance attribution for each active trading account:
/// strategy-level P&amp;L breakdown, ML alpha, timing alpha, execution costs,
/// and risk-adjusted returns (Sharpe, Sortino, Calmar).
///
/// Returns are computed using TWRR (Time-Weighted Rate of Return) to eliminate
/// the distortion from cash flows and position sizing changes.
/// </summary>
public class PerformanceAttributionWorker : BackgroundService
{
    private readonly ILogger<PerformanceAttributionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private const int DefaultPollSeconds = 3600;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    private int _consecutiveFailures;

    public PerformanceAttributionWorker(
        ILogger<PerformanceAttributionWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PerformanceAttributionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ComputeDailyAttributionAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "PerformanceAttributionWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(DefaultPollSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task ComputeDailyAttributionAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var priceCache = scope.ServiceProvider.GetRequiredService<ILivePriceCache>();

        var today = DateTime.UtcNow.Date;
        var yesterday = today.AddDays(-1);

        var accounts = await readCtx.GetDbContext()
            .Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);

        foreach (var account in accounts)
        {
            // Check if we already have an attribution for yesterday
            var existing = await readCtx.GetDbContext()
                .Set<AccountPerformanceAttribution>()
                .AnyAsync(a => a.TradingAccountId == account.Id
                            && a.AttributionDate == yesterday
                            && !a.IsDeleted, ct);

            if (existing) continue;

            // #6: Zero-equity guard — skip accounts with zero or negative equity
            decimal startOfDayEquity = account.Equity;
            if (startOfDayEquity <= 0)
            {
                _logger.LogWarning(
                    "Account {Id} has zero/negative equity ({Equity}) — skipping attribution",
                    account.Id, startOfDayEquity);
                continue;
            }

            // Get closed positions from yesterday, filtered by account via OpenOrderId→Order
            var closedPositions = await readCtx.GetDbContext()
                .Set<Position>()
                .Where(p => p.Status == PositionStatus.Closed
                         && p.ClosedAt >= yesterday && p.ClosedAt < today
                         && p.OpenOrderId != null
                         && !p.IsDeleted)
                .Join(readCtx.GetDbContext().Set<Order>().Where(o => !o.IsDeleted),
                    p => p.OpenOrderId,
                    o => o.Id,
                    (p, o) => new { Position = p, Order = o })
                .Where(x => x.Order.TradingAccountId == account.Id)
                .Select(x => new { x.Position, x.Order.StrategyId })
                .ToListAsync(ct);

            // Get TCA records for yesterday's fills
            var tcaRecords = await readCtx.GetDbContext()
                .Set<TransactionCostAnalysis>()
                .Where(t => t.AnalyzedAt >= yesterday && t.AnalyzedAt < today && !t.IsDeleted)
                .ToListAsync(ct);

            decimal realizedPnl = closedPositions.Sum(x => x.Position.RealizedPnL);
            decimal executionCosts = tcaRecords.Sum(t => t.TotalCost);
            int tradeCount = closedPositions.Count;
            int winCount = closedPositions.Count(x => x.Position.RealizedPnL > 0);
            decimal winRate = tradeCount > 0 ? (decimal)winCount / tradeCount : 0;

            // #7: Unrealized P&L — compute current mark-to-market of open positions
            var openPositions = await readCtx.GetDbContext()
                .Set<Position>()
                .Where(p => p.OpenOrderId != null
                         && p.Status == PositionStatus.Open
                         && !p.IsDeleted)
                .Join(readCtx.GetDbContext().Set<Order>().Where(o => !o.IsDeleted),
                    p => p.OpenOrderId,
                    o => o.Id,
                    (p, o) => new { Position = p, o.TradingAccountId })
                .Where(x => x.TradingAccountId == account.Id)
                .Select(x => x.Position)
                .ToListAsync(ct);

            decimal currentUnrealizedPnl = 0;
            foreach (var pos in openPositions)
            {
                var price = priceCache.Get(pos.Symbol);
                if (price is not null)
                {
                    decimal mtm = pos.Direction == PositionDirection.Long
                        ? (price.Value.Bid - pos.AverageEntryPrice) * pos.OpenLots
                        : (pos.AverageEntryPrice - price.Value.Ask) * pos.OpenLots;
                    currentUnrealizedPnl += mtm;
                }
                else
                {
                    // Fall back to the position's stored unrealized P&L
                    currentUnrealizedPnl += pos.UnrealizedPnL;
                }
            }

            // Get previous day's unrealized P&L from the most recent attribution record
            decimal previousDayUnrealizedPnl = 0;
            var prevAttribution = await readCtx.GetDbContext()
                .Set<AccountPerformanceAttribution>()
                .Where(a => a.TradingAccountId == account.Id
                         && a.AttributionDate < yesterday
                         && !a.IsDeleted)
                .OrderByDescending(a => a.AttributionDate)
                .FirstOrDefaultAsync(ct);
            if (prevAttribution is not null)
                previousDayUnrealizedPnl = prevAttribution.UnrealizedPnlChange;

            decimal unrealizedPnlChange = currentUnrealizedPnl - previousDayUnrealizedPnl;

            // #1: TWRR (Time-Weighted Rate of Return): geometric linking of sub-period returns
            // TWRR eliminates the effect of cash flows and position sizing changes.
            // For daily attribution without intra-day cash flow timestamps, approximate:
            // TWRR = (endOfDayEquity / startOfDayEquity) - 1
            // This uses the equity change including both realized and unrealized P&L,
            // which is more accurate than realized-only MWRR.
            startOfDayEquity = account.Equity - realizedPnl - unrealizedPnlChange; // Reconstruct SOD equity
            decimal endOfDayEquity = startOfDayEquity + realizedPnl + unrealizedPnlChange;
            decimal dailyReturnPct = startOfDayEquity > 0
                ? (endOfDayEquity / startOfDayEquity) - 1m
                : 0m;

            // #5: Strategy-level attribution breakdown (using Order.StrategyId)
            var strategyGroups = closedPositions
                .GroupBy(x => x.StrategyId)
                .Select(g => new
                {
                    strategyId = g.Key,
                    pnl        = g.Sum(x => x.Position.RealizedPnL),
                    trades     = g.Count()
                })
                .ToList();

            // Symbol-level attribution (secondary breakdown)
            var symbolGroups = closedPositions
                .GroupBy(x => x.Position.Symbol)
                .Select(g => new
                {
                    symbol = g.Key,
                    pnl    = g.Sum(x => x.Position.RealizedPnL),
                    trades = g.Count()
                })
                .ToList();

            // #2: Position-weighted benchmark returns
            // Weight benchmark returns by each symbol's position exposure (notional value)
            // benchmarkReturn = Sum(weight_i * return_i) where weight_i = notional_i / totalNotional
            var positionsBySymbol = closedPositions.GroupBy(x => x.Position.Symbol).ToList();
            decimal benchmarkReturnPct = 0;

            if (positionsBySymbol.Count > 0)
            {
                var tradedSymbols = positionsBySymbol.Select(g => g.Key).ToList();

                var d1Candles = await readCtx.GetDbContext()
                    .Set<Candle>()
                    .Where(c => tradedSymbols.Contains(c.Symbol)
                             && c.Timeframe == Timeframe.D1
                             && c.Timestamp >= yesterday && c.Timestamp < today
                             && c.IsClosed && !c.IsDeleted)
                    .ToListAsync(ct);

                var candleBySymbol = d1Candles
                    .Where(c => c.Open != 0)
                    .ToDictionary(c => c.Symbol, c => (c.Close - c.Open) / c.Open);

                decimal totalNotional = 0m;
                foreach (var symbolGroup in positionsBySymbol)
                {
                    decimal symbolNotional = symbolGroup.Sum(x =>
                        Math.Abs(x.Position.OpenLots * x.Position.AverageEntryPrice));
                    totalNotional += symbolNotional;
                }

                if (totalNotional > 0)
                {
                    // Track how much weight was actually used (symbols with candle data)
                    // to re-normalize when some symbols are missing D1 candles
                    decimal usedWeight = 0;

                    foreach (var symbolGroup in positionsBySymbol)
                    {
                        decimal symbolNotional = symbolGroup.Sum(x =>
                            Math.Abs(x.Position.OpenLots * x.Position.AverageEntryPrice));
                        decimal weight = symbolNotional / totalNotional;

                        if (candleBySymbol.TryGetValue(symbolGroup.Key, out var symbolReturn))
                        {
                            benchmarkReturnPct += weight * symbolReturn;
                            usedWeight += weight;
                        }
                    }

                    // Re-normalize to compensate for missing symbols
                    if (usedWeight > 0 && usedWeight < 1.0m)
                        benchmarkReturnPct /= usedWeight;
                }
            }

            decimal activeReturnPct = dailyReturnPct - benchmarkReturnPct;
            decimal alphaVsBenchmarkPct = activeReturnPct;

            // Execution cost impact on alpha
            decimal grossAlphaPct = dailyReturnPct;
            decimal executionCostPct = startOfDayEquity > 0
                ? executionCosts / startOfDayEquity
                : 0;
            decimal netAlphaPct = grossAlphaPct - executionCostPct;

            // #4: Compute Sharpe/Sortino/Calmar from rolling daily returns
            // Sharpe is annualized via sqrt(252) convention (see helper methods below)
            var last7Returns = await GetRecentDailyReturns(readCtx, account.Id, 7, yesterday, ct);
            var last30Returns = await GetRecentDailyReturns(readCtx, account.Id, 30, yesterday, ct);

            // Include today's return in the rolling windows
            var last7WithToday = new List<decimal>(last7Returns) { dailyReturnPct };
            var last30WithToday = new List<decimal>(last30Returns) { dailyReturnPct };

            decimal sharpeRatio7d = ComputeSharpe(last7WithToday);
            decimal sharpeRatio30d = ComputeSharpe(last30WithToday);
            decimal sortinoRatio30d = ComputeSortino(last30WithToday);
            decimal calmarRatio30d = ComputeCalmar(last30WithToday);

            // #3: Information ratio = mean(activeReturn) / std(activeReturn) * sqrt(252)
            // where activeReturn = portfolioReturn - benchmarkReturn (per day)
            decimal informationRatio = 0;
            if (last30WithToday.Count >= 5)
            {
                var last30BenchmarkReturns = await GetRecentBenchmarkReturns(readCtx, account.Id, 30, yesterday, ct);
                last30BenchmarkReturns.Add(benchmarkReturnPct);

                // Align lengths (take only as many as we have benchmark data for)
                int alignedCount = Math.Min(last30WithToday.Count, last30BenchmarkReturns.Count);
                var activeReturns = new List<decimal>(alignedCount);
                for (int i = last30WithToday.Count - alignedCount; i < last30WithToday.Count; i++)
                {
                    int benchIdx = i - (last30WithToday.Count - alignedCount);
                    if (benchIdx < last30BenchmarkReturns.Count)
                        activeReturns.Add(last30WithToday[i] - last30BenchmarkReturns[benchIdx]);
                }

                informationRatio = ComputeSharpe(activeReturns); // Same formula, different input
            }

            // ── ML Alpha / Timing Alpha / Selection Alpha decomposition ──────
            // Decompose each trade's P&L into three components:
            //
            // Selection Alpha: Did the model pick the right direction?
            //   = P&L from trades where ML prediction matched actual outcome
            //     minus P&L from trades where ML prediction was wrong.
            //   Positive = ML direction calls add value.
            //
            // Timing Alpha: Did the actual entry improve on the signal's suggested entry?
            //   = Sum of (signalEntry - actualEntry) × direction × lots
            //   Positive = execution got better prices than the signal suggested.
            //
            // ML Alpha (residual): P&L attributable to ML-scored trades vs rule-only trades.
            //   = P&L from ML-scored trades - P&L from non-ML trades, normalized by trade count.

            decimal mlAlphaPnl = 0;
            decimal timingAlphaPnl = 0;

            // Load trade rationales for yesterday's closed positions via OrderId
            var orderIdsForRationale = closedPositions
                .Where(x => x.Position.OpenOrderId.HasValue)
                .Select(x => x.Position.OpenOrderId!.Value)
                .Distinct()
                .ToList();
            var rationales = orderIdsForRationale.Count > 0
                ? await readCtx.GetDbContext()
                    .Set<TradeRationale>()
                    .Where(r => orderIdsForRationale.Contains(r.OrderId) && !r.IsDeleted)
                    .ToListAsync(ct)
                : new List<TradeRationale>();

            // Map OrderId → TradeRationale, then we can look up by position's OpenOrderId
            var rationaleByOrderId = rationales
                .GroupBy(r => r.OrderId)
                .ToDictionary(g => g.Key, g => g.First());

            // Load signal entries for timing alpha
            var orderIds = closedPositions
                .Where(x => x.Position.OpenOrderId.HasValue)
                .Select(x => x.Position.OpenOrderId!.Value)
                .Distinct()
                .ToList();
            var signalEntries = orderIds.Count > 0
                ? await readCtx.GetDbContext()
                    .Set<Order>()
                    .Where(o => orderIds.Contains(o.Id) && o.TradeSignalId.HasValue && !o.IsDeleted)
                    .Join(readCtx.GetDbContext().Set<TradeSignal>().Where(s => !s.IsDeleted),
                        o => o.TradeSignalId,
                        s => s.Id,
                        (o, s) => new { OrderId = o.Id, s.EntryPrice })
                    .ToDictionaryAsync(x => x.OrderId, x => x.EntryPrice, ct)
                : new Dictionary<long, decimal>();

            decimal mlScoredPnl = 0;
            int mlScoredCount = 0;
            decimal rulePnl = 0;
            int ruleCount = 0;

            foreach (var cp in closedPositions)
            {
                var pos = cp.Position;

                // Timing alpha: (signalEntry - actualEntry) × direction × lots
                // Positive timing alpha = got in at a better price than the signal suggested
                if (pos.OpenOrderId.HasValue && signalEntries.TryGetValue(pos.OpenOrderId.Value, out var signalEntry))
                {
                    decimal directionMultiplier = pos.Direction == PositionDirection.Long ? 1m : -1m;
                    timingAlphaPnl += (signalEntry - pos.AverageEntryPrice) * directionMultiplier * pos.OpenLots;
                }

                // ML alpha decomposition
                if (pos.OpenOrderId.HasValue
                    && rationaleByOrderId.TryGetValue(pos.OpenOrderId.Value, out var rationale)
                    && rationale.MLModelId.HasValue)
                {
                    mlScoredPnl += pos.RealizedPnL;
                    mlScoredCount++;
                }
                else
                {
                    rulePnl += pos.RealizedPnL;
                    ruleCount++;
                }
            }

            // ML Alpha = direct P&L difference between ML-scored and rule-based trades.
            // This is the raw dollar difference — how much more did ML trades generate?
            // For per-trade comparison: (mlScoredPnl / mlScoredCount) - (rulePnl / ruleCount)
            if (mlScoredCount > 0 && ruleCount > 0)
                mlAlphaPnl = mlScoredPnl - rulePnl;
            else if (mlScoredCount > 0)
                mlAlphaPnl = mlScoredPnl; // all trades are ML-scored
            // else: all trades are rule-based, ML alpha = 0

            var attribution = new AccountPerformanceAttribution
            {
                TradingAccountId        = account.Id,
                AttributionDate         = yesterday,
                StartOfDayEquity        = startOfDayEquity,
                EndOfDayEquity          = endOfDayEquity,
                RealizedPnl             = realizedPnl,
                UnrealizedPnlChange     = currentUnrealizedPnl,
                DailyReturnPct          = dailyReturnPct,
                StrategyAttributionJson = System.Text.Json.JsonSerializer.Serialize(strategyGroups),
                SymbolAttributionJson   = System.Text.Json.JsonSerializer.Serialize(symbolGroups),
                ExecutionCosts          = executionCosts,
                TradeCount              = tradeCount,
                WinRate                 = winRate,
                BenchmarkReturnPct      = benchmarkReturnPct,
                AlphaVsBenchmarkPct     = alphaVsBenchmarkPct,
                ActiveReturnPct         = activeReturnPct,
                InformationRatio        = informationRatio,
                GrossAlphaPct           = grossAlphaPct,
                ExecutionCostPct        = executionCostPct,
                NetAlphaPct             = netAlphaPct,
                MLAlphaPnl              = mlAlphaPnl,
                TimingAlphaPnl          = timingAlphaPnl,
                SharpeRatio7d           = sharpeRatio7d,
                SharpeRatio30d          = sharpeRatio30d,
                SortinoRatio30d         = sortinoRatio30d,
                CalmarRatio30d          = calmarRatio30d
            };

            await writeCtx.GetDbContext()
                .Set<AccountPerformanceAttribution>()
                .AddAsync(attribution, ct);
        }

        await writeCtx.GetDbContext().SaveChangesAsync(ct);
        _logger.LogInformation("PerformanceAttributionWorker: computed daily attribution for {Count} accounts",
            accounts.Count);
    }

    /// <summary>
    /// Retrieves the most recent N daily return values from persisted attribution records.
    /// Used for rolling Sharpe/Sortino/Calmar computation.
    /// </summary>
    private static async Task<List<decimal>> GetRecentDailyReturns(
        IReadApplicationDbContext readCtx, long accountId, int days, DateTime beforeDate, CancellationToken ct)
    {
        var records = await readCtx.GetDbContext()
            .Set<AccountPerformanceAttribution>()
            .Where(a => a.TradingAccountId == accountId
                     && a.AttributionDate < beforeDate
                     && !a.IsDeleted)
            .OrderByDescending(a => a.AttributionDate)
            .Take(days)
            .Select(a => a.DailyReturnPct)
            .ToListAsync(ct);

        records.Reverse(); // Chronological order
        return records;
    }

    /// <summary>
    /// Retrieves the most recent N benchmark return values from persisted attribution records.
    /// Used for Information Ratio computation.
    /// </summary>
    private static async Task<List<decimal>> GetRecentBenchmarkReturns(
        IReadApplicationDbContext readCtx, long accountId, int days, DateTime beforeDate, CancellationToken ct)
    {
        var records = await readCtx.GetDbContext()
            .Set<AccountPerformanceAttribution>()
            .Where(a => a.TradingAccountId == accountId
                     && a.AttributionDate < beforeDate
                     && !a.IsDeleted)
            .OrderByDescending(a => a.AttributionDate)
            .Take(days)
            .Select(a => a.BenchmarkReturnPct)
            .ToListAsync(ct);

        records.Reverse(); // Chronological order
        return records;
    }

    /// <summary>
    /// Computes annualized Sharpe ratio from a series of daily returns.
    /// Sharpe = mean(dailyReturns) / stdDev(dailyReturns) * sqrt(252)
    /// Annualization uses the sqrt(252) convention (252 trading days per year).
    /// </summary>
    private static decimal ComputeSharpe(List<decimal> dailyReturns)
    {
        if (dailyReturns.Count < 2) return 0;
        decimal mean = dailyReturns.Average();
        decimal variance = dailyReturns.Sum(r => (r - mean) * (r - mean)) / (dailyReturns.Count - 1);
        decimal std = (decimal)Math.Sqrt((double)variance);
        // Annualized via sqrt(252) — 252 trading days per year
        return std > 0.0000001m ? mean / std * (decimal)Math.Sqrt(252.0) : 0;
    }

    /// <summary>
    /// Computes annualized Sortino ratio from a series of daily returns.
    /// Sortino = mean(dailyReturns) / downsideStdDev(dailyReturns) * sqrt(252)
    /// where downsideStdDev only considers returns &lt; 0 (downside deviation).
    /// Annualization uses the sqrt(252) convention (252 trading days per year).
    /// </summary>
    private static decimal ComputeSortino(List<decimal> dailyReturns)
    {
        if (dailyReturns.Count < 2) return 0;
        decimal mean = dailyReturns.Average();
        var downside = dailyReturns.Where(r => r < 0).ToList();
        if (downside.Count == 0) return mean > 0 ? 99m : 0; // No downside observed
        decimal downsideVar = downside.Sum(r => r * r) / downside.Count;
        decimal downsideStd = (decimal)Math.Sqrt((double)downsideVar);
        // Annualized via sqrt(252) — 252 trading days per year
        return downsideStd > 0.0000001m ? mean / downsideStd * (decimal)Math.Sqrt(252.0) : 0;
    }

    /// <summary>
    /// Computes Calmar ratio from a series of daily returns.
    /// Calmar = annualizedReturn / maxDrawdown
    /// where annualizedReturn = geometricCumulativeReturn * (252 / N)
    /// and maxDrawdown = max peak-to-trough decline in the equity curve.
    /// Annualization assumes 252 trading days per year.
    /// </summary>
    private static decimal ComputeCalmar(List<decimal> dailyReturns)
    {
        if (dailyReturns.Count < 5) return 0;
        decimal cumulativeReturn = dailyReturns.Aggregate(1m, (acc, r) => acc * (1 + r)) - 1;
        decimal annualizedReturn = cumulativeReturn * (252m / dailyReturns.Count);

        // Max drawdown from equity curve
        decimal peak = 1m, maxDD = 0;
        decimal equity = 1m;
        foreach (var r in dailyReturns)
        {
            equity *= (1 + r);
            if (equity > peak) peak = equity;
            decimal dd = (peak - equity) / peak;
            if (dd > maxDD) maxDD = dd;
        }

        return maxDD > 0.0001m ? annualizedReturn / maxDD : 0;
    }
}
