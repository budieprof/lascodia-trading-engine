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

        var now = DateTime.UtcNow;
        // Truncate to the current hour for the attribution timestamp
        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var dayStart  = now.Date;

        var accounts = await readCtx.GetDbContext()
            .Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);

        var processedCount = 0;

        foreach (var account in accounts)
        {
            // Check if we already have an attribution for this hour
            var existing = await readCtx.GetDbContext()
                .Set<AccountPerformanceAttribution>()
                .AnyAsync(a => a.TradingAccountId == account.Id
                            && a.AttributionDate == hourStart
                            && !a.IsDeleted, ct);

            if (existing) continue;

            // Get positions closed today (up to now)
            var closedPositions = await readCtx.GetDbContext()
                .Set<Position>()
                .Where(p => p.Status == PositionStatus.Closed
                         && p.ClosedAt >= dayStart && p.ClosedAt <= now
                         && !p.IsDeleted)
                .ToListAsync(ct);

            // Get open positions for unrealized P&L
            var openPositions = await readCtx.GetDbContext()
                .Set<Position>()
                .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
                .ToListAsync(ct);

            // Get TCA records for today
            var tcaRecords = await readCtx.GetDbContext()
                .Set<TransactionCostAnalysis>()
                .Where(t => t.AnalyzedAt >= dayStart && t.AnalyzedAt <= now && !t.IsDeleted)
                .ToListAsync(ct);

            decimal realizedPnl = closedPositions.Sum(p => p.RealizedPnL);
            decimal executionCosts = tcaRecords.Sum(t => t.TotalCost);
            int tradeCount = closedPositions.Count;
            int winCount = closedPositions.Count(p => p.RealizedPnL > 0);
            decimal winRate = tradeCount > 0 ? (decimal)winCount / tradeCount : 0;

            decimal startOfDayEquity = account.Equity - realizedPnl; // Approximate
            decimal dailyReturnPct = startOfDayEquity > 0
                ? realizedPnl / startOfDayEquity * 100m
                : 0;

            // Strategy/symbol attribution breakdown
            var symbolGroups = closedPositions
                .GroupBy(p => p.Symbol)
                .Select(g => new
                {
                    symbol = g.Key,
                    pnl    = g.Sum(p => p.RealizedPnL),
                    trades = g.Count()
                })
                .ToList();

            // Execution cost impact
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
                DailyReturnPct          = dailyReturnPct,
                StrategyAttributionJson = System.Text.Json.JsonSerializer.Serialize(symbolGroups),
                SymbolAttributionJson   = System.Text.Json.JsonSerializer.Serialize(symbolGroups),
                ExecutionCosts          = executionCosts,
                TradeCount              = tradeCount,
                WinRate                 = winRate,
                GrossAlphaPct           = grossAlphaPct,
                ExecutionCostPct        = executionCostPct,
                NetAlphaPct             = netAlphaPct
            };

            await writeCtx.GetDbContext()
                .Set<AccountPerformanceAttribution>()
                .AddAsync(attribution, ct);

            processedCount++;
        }

        if (processedCount > 0)
        {
            await writeCtx.GetDbContext().SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "IntradayAttributionWorker: computed hourly attribution for {Count} accounts at {Hour}",
            processedCount, hourStart);
    }
}
