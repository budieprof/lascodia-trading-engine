using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.PaperTrading.Commands.BackfillPaperExecutions;

/// <summary>
/// Operator-invoked tool that replays an Approved-but-not-Active strategy against
/// historical candles through the live BacktestEngine (with the current TCA profile)
/// and writes the resulting simulated trades as <see cref="PaperExecution"/> rows
/// tagged <see cref="PaperExecution.IsSynthetic"/> = true.
///
/// <para>
/// Does NOT change promotion-gate decisions: the gate already excludes synthetic rows.
/// Purpose is observability — give operators a way to see what the paper pipeline
/// would have produced over a historical window without waiting for live data to accrue.
/// For real forward-test data the signal must run live through the paper router.
/// </para>
/// </summary>
public class BackfillPaperExecutionsCommand : IRequest<ResponseData<int>>
{
    public required long StrategyId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc   { get; init; }
}

public class BackfillPaperExecutionsCommandHandler
    : IRequestHandler<BackfillPaperExecutionsCommand, ResponseData<int>>
{
    private readonly IWriteApplicationDbContext _writeCtx;
    private readonly IBacktestEngine _backtestEngine;
    private readonly IBacktestOptionsSnapshotBuilder _optionsSnapshotBuilder;
    private readonly ITcaCostModelProvider _tcaProvider;

    public BackfillPaperExecutionsCommandHandler(
        IWriteApplicationDbContext writeCtx,
        IBacktestEngine backtestEngine,
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        ITcaCostModelProvider tcaProvider)
    {
        _writeCtx               = writeCtx;
        _backtestEngine         = backtestEngine;
        _optionsSnapshotBuilder = optionsSnapshotBuilder;
        _tcaProvider            = tcaProvider;
    }

    public async Task<ResponseData<int>> Handle(BackfillPaperExecutionsCommand req, CancellationToken ct)
    {
        var db = _writeCtx.GetDbContext();

        var strategy = await db.Set<Strategy>()
            .FirstOrDefaultAsync(s => s.Id == req.StrategyId && !s.IsDeleted, ct);
        if (strategy is null)
            return ResponseData<int>.Init(0, false, "Strategy not found", "-14");

        var fromUtc = req.FromUtc ?? strategy.LifecycleStageEnteredAt ?? DateTime.UtcNow.AddDays(-30);
        var toUtc   = req.ToUtc   ?? DateTime.UtcNow;

        var candles = await db.Set<Candle>().AsNoTracking()
            .Where(c => c.Symbol == strategy.Symbol
                     && c.Timeframe == strategy.Timeframe
                     && c.Timestamp >= fromUtc
                     && c.Timestamp <= toUtc
                     && c.IsClosed
                     && !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        if (candles.Count == 0)
            return ResponseData<int>.Init(0, false,
                $"No closed candles for {strategy.Symbol}/{strategy.Timeframe} in [{fromUtc:u}, {toUtc:u}]", "-15");

        var snapshot      = await _optionsSnapshotBuilder.BuildAsync(db, strategy.Symbol, ct);
        var options       = snapshot.ToOptions();
        options.TcaProfile = await _tcaProvider.GetAsync(strategy.Symbol, ct);

        var result = await _backtestEngine.RunAsync(strategy, candles, initialBalance: 10_000m, ct, options);

        var pair = await db.Set<CurrencyPair>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Symbol == strategy.Symbol && !p.IsDeleted, ct);
        decimal contractSize = pair?.ContractSize ?? 100_000m;
        decimal pipSize      = pair is { PipSize: > 0 } ? pair.PipSize
                             : pair is { DecimalPlaces: > 0 }
                                ? (decimal)Math.Pow(10, -(pair.DecimalPlaces - 1))
                                : 0.0001m;

        string tcaJson = JsonSerializer.Serialize(options.TcaProfile);

        int inserted = 0;
        foreach (var trade in result.Trades)
        {
            var row = new PaperExecution
            {
                StrategyId                    = strategy.Id,
                Symbol                        = strategy.Symbol,
                Timeframe                     = strategy.Timeframe,
                Direction                     = trade.Direction,
                SignalGeneratedAt             = trade.EntryTime,
                RequestedEntryPrice           = trade.EntryPrice,
                SimulatedFillPrice            = trade.EntryPrice,
                SimulatedFillAt               = trade.EntryTime,
                SimulatedSlippagePriceUnits   = 0m,
                SimulatedSpreadCostPriceUnits = 0m,
                SimulatedCommissionAccountCcy = trade.Commission,
                LotSize                       = trade.LotSize,
                ContractSize                  = contractSize,
                PipSize                       = pipSize,
                SimulatedExitPrice            = trade.ExitPrice,
                SimulatedExitReason           = trade.ExitReason switch
                {
                    TradeExitReason.StopLoss   => PaperExitReason.StopLoss,
                    TradeExitReason.TakeProfit => PaperExitReason.TakeProfit,
                    _                          => PaperExitReason.EndOfEvaluation,
                },
                SimulatedGrossPnL             = trade.GrossPnL,
                SimulatedNetPnL               = trade.PnL,
                ClosedAt                      = trade.ExitTime,
                Status                        = PaperExecutionStatus.Closed,
                IsSynthetic                   = true,
                TcaProfileSnapshotJson        = tcaJson,
            };
            await db.Set<PaperExecution>().AddAsync(row, ct);
            inserted++;
        }

        await _writeCtx.SaveChangesAsync(ct);

        return ResponseData<int>.Init(inserted, true,
            $"Backfilled {inserted} synthetic PaperExecution rows for strategy {strategy.Id}", "00");
    }
}
