using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically closes <see cref="PaperExecution"/> rows that hit SL/TP or aged past the
/// per-signal timeout. Runs on a 5-second tick — each cycle pulls open rows, joins against
/// the latest live-price cache, and resolves brackets using the same math as BacktestEngine.
///
/// <para>
/// Deliberately a polling worker, not a per-tick reactor. A reactor would fire on every
/// `PriceUpdatedIntegrationEvent` and do a DB roundtrip per event — wasteful when most
/// ticks don't breach any bracket. Polling every 5 s keeps the DB load to roughly 1 query
/// per 5 s regardless of tick rate, and SL/TP resolution 5-second-granular is adequate for
/// a forward-test signal that already has execution-latency baked into the TCA profile.
/// </para>
/// </summary>
public sealed class PaperExecutionMonitorWorker : BackgroundService
{
    private readonly ILogger<PaperExecutionMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILivePriceCache _cache;

    private static readonly TimeSpan PollInterval     = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SignalTimeout    = TimeSpan.FromHours(48);
    private static readonly TimeSpan SweepCadence     = TimeSpan.FromMinutes(5);
    private DateTime _nextSweepAt = DateTime.UtcNow.Add(SweepCadence);

    public PaperExecutionMonitorWorker(
        ILogger<PaperExecutionMonitorWorker> logger,
        IServiceScopeFactory scopeFactory,
        ILivePriceCache cache)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _cache        = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaperExecutionMonitorWorker starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ResolveOpenRowsAsync(stoppingToken);
                if (DateTime.UtcNow >= _nextSweepAt)
                {
                    await ExpireStaleRowsAsync(stoppingToken);
                    _nextSweepAt = DateTime.UtcNow.Add(SweepCadence);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PaperExecutionMonitorWorker: cycle error");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ResolveOpenRowsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db       = writeCtx.GetDbContext();

        var openRows = await db.Set<PaperExecution>()
            .Where(p => p.Status == PaperExecutionStatus.Open && !p.IsDeleted)
            .ToListAsync(ct);

        if (openRows.Count == 0) return;

        int closed = 0;
        foreach (var row in openRows)
        {
            if (ct.IsCancellationRequested) break;
            var price = _cache.Get(row.Symbol);
            if (price is null) continue;

            // Long: close at bid; short: close at ask.
            decimal exitQuote = row.Direction == TradeDirection.Buy ? price.Value.Bid : price.Value.Ask;

            // Track MAE/MFE for observability.
            decimal absMove = row.Direction == TradeDirection.Buy
                ? exitQuote - row.SimulatedFillPrice
                : row.SimulatedFillPrice - exitQuote;
            if (absMove < (row.SimulatedMaeAbsolute ?? 0m) || row.SimulatedMaeAbsolute is null)
                row.SimulatedMaeAbsolute = absMove < 0 ? absMove : row.SimulatedMaeAbsolute ?? 0m;
            if (absMove > (row.SimulatedMfeAbsolute ?? 0m))
                row.SimulatedMfeAbsolute = absMove > 0 ? absMove : row.SimulatedMfeAbsolute ?? 0m;

            PaperExitReason? reason = null;
            decimal? exitPrice = null;

            if (row.Direction == TradeDirection.Buy)
            {
                if (row.StopLoss is { } sl && exitQuote <= sl)
                {
                    reason = PaperExitReason.StopLoss;
                    exitPrice = sl;
                }
                else if (row.TakeProfit is { } tp && exitQuote >= tp)
                {
                    reason = PaperExitReason.TakeProfit;
                    exitPrice = tp;
                }
            }
            else
            {
                if (row.StopLoss is { } sl && exitQuote >= sl)
                {
                    reason = PaperExitReason.StopLoss;
                    exitPrice = sl;
                }
                else if (row.TakeProfit is { } tp && exitQuote <= tp)
                {
                    reason = PaperExitReason.TakeProfit;
                    exitPrice = tp;
                }
            }

            if (reason is null) continue;

            // Compute P&L. Same formula as BacktestEngine.CalculatePnL.
            decimal priceMove = row.Direction == TradeDirection.Buy
                ? exitPrice!.Value - row.SimulatedFillPrice
                : row.SimulatedFillPrice - exitPrice!.Value;
            decimal grossPnl  = priceMove * row.LotSize * row.ContractSize;
            decimal netPnl    = grossPnl - row.SimulatedCommissionAccountCcy;

            row.SimulatedExitPrice   = exitPrice;
            row.SimulatedExitReason  = reason;
            row.SimulatedGrossPnL    = grossPnl;
            row.SimulatedNetPnL      = netPnl;
            row.ClosedAt             = DateTime.UtcNow;
            row.Status               = PaperExecutionStatus.Closed;
            closed++;
        }

        if (closed > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogDebug("PaperExecutionMonitorWorker: closed {Count} rows", closed);
        }
    }

    private async Task ExpireStaleRowsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db       = writeCtx.GetDbContext();

        var cutoff = DateTime.UtcNow.Subtract(SignalTimeout);
        var expired = await db.Set<PaperExecution>()
            .Where(p => p.Status == PaperExecutionStatus.Open
                     && !p.IsDeleted
                     && p.SignalGeneratedAt < cutoff)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        foreach (var row in expired)
        {
            row.Status              = PaperExecutionStatus.Expired;
            row.SimulatedExitReason = PaperExitReason.Timeout;
            row.ClosedAt            = DateTime.UtcNow;
            // No net PnL recorded — treat as inconclusive (exit price unknown).
        }
        await writeCtx.SaveChangesAsync(ct);
        _logger.LogInformation("PaperExecutionMonitorWorker: expired {Count} timeout rows", expired.Count);
    }
}
