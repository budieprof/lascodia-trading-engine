using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.PaperTrading.Services;

/// <summary>
/// Opens a <see cref="PaperExecution"/> row for a signal produced by an approved-but-not-
/// active strategy. Applies realised TCA slippage/spread/commission from
/// <see cref="ITcaCostModelProvider"/> so paper fills are directly comparable to live fills.
/// The monitor worker then closes the row on SL/TP/Timeout from subsequent tick data.
/// </summary>
public interface IPaperExecutionRouter
{
    Task EnqueueAsync(
        Strategy strategy,
        PaperSignalIntent signal,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken ct);
}

/// <summary>
/// Decoupled signal payload so the router doesn't depend on `EvaluatorSignal` or any
/// strategy-specific DTO. Upstream builds this from whatever the strategy evaluator produced.
/// </summary>
public sealed record PaperSignalIntent(
    TradeDirection Direction,
    decimal        RequestedEntryPrice,
    decimal        LotSize,
    decimal?       StopLoss,
    decimal?       TakeProfit,
    DateTime       GeneratedAtUtc,
    long?          TradeSignalId);

[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
public sealed class PaperExecutionRouter : IPaperExecutionRouter
{
    private readonly IWriteApplicationDbContext _writeCtx;
    private readonly ITcaCostModelProvider _tcaProvider;
    private readonly ILogger<PaperExecutionRouter> _logger;

    private const int    MaxOpenPerStrategyDefault = 10;
    private const int    BacklogPauseDefault       = 50;

    public PaperExecutionRouter(
        IWriteApplicationDbContext writeCtx,
        ITcaCostModelProvider tcaProvider,
        ILogger<PaperExecutionRouter> logger)
    {
        _writeCtx    = writeCtx;
        _tcaProvider = tcaProvider;
        _logger      = logger;
    }

    public async Task EnqueueAsync(
        Strategy strategy,
        PaperSignalIntent signal,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken ct)
    {
        var db = _writeCtx.GetDbContext();

        int openCount = await db.Set<PaperExecution>()
            .AsNoTracking()
            .CountAsync(p => p.StrategyId == strategy.Id
                          && !p.IsDeleted
                          && p.Status == PaperExecutionStatus.Open, ct);

        if (openCount >= BacklogPauseDefault)
        {
            _logger.LogWarning(
                "PaperExecutionRouter: strategy {Id} has {Count} open paper rows — dropping new signal (circuit open)",
                strategy.Id, openCount);
            return;
        }
        if (openCount >= MaxOpenPerStrategyDefault)
        {
            _logger.LogDebug(
                "PaperExecutionRouter: strategy {Id} at MaxOpenPerStrategy cap ({Cap}); new signal deferred",
                strategy.Id, MaxOpenPerStrategyDefault);
            return;
        }

        var profile = await _tcaProvider.GetAsync(strategy.Symbol, ct);

        // Apply half-spread + slippage against the trader direction, exactly mirroring
        // BacktestEngine.cs fill semantics so paper and backtest Sharpes are apples-to-apples.
        decimal halfSpread = profile.AvgSpreadCostInPrice / 2m;
        decimal slippage   = profile.AvgMarketImpactInPrice;

        decimal fillPrice = signal.Direction == TradeDirection.Buy
            ? currentPrice.Ask + halfSpread + slippage
            : currentPrice.Bid - halfSpread - slippage;

        // CurrencyPair spec lookup (for ContractSize / PipSize). Falls back to FX defaults.
        var pair = await db.Set<CurrencyPair>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Symbol == strategy.Symbol && !p.IsDeleted, ct);
        decimal contractSize = pair?.ContractSize ?? 100_000m;
        decimal pipSize      = pair is { PipSize: > 0 } ? pair.PipSize
                             : pair is { DecimalPlaces: > 0 }
                                ? (decimal)Math.Pow(10, -(pair.DecimalPlaces - 1))
                                : 0.0001m;

        var row = new PaperExecution
        {
            StrategyId                    = strategy.Id,
            TradeSignalId                 = signal.TradeSignalId,
            Symbol                        = strategy.Symbol,
            Timeframe                     = strategy.Timeframe,
            Direction                     = signal.Direction,
            SignalGeneratedAt             = signal.GeneratedAtUtc,
            RequestedEntryPrice           = signal.RequestedEntryPrice,
            SimulatedFillPrice            = fillPrice,
            SimulatedFillAt               = DateTime.UtcNow,
            SimulatedSlippagePriceUnits   = slippage,
            SimulatedSpreadCostPriceUnits = halfSpread * 2m,
            SimulatedCommissionAccountCcy = profile.AvgCommissionCostInAccountCcy * signal.LotSize * contractSize,
            LotSize                       = signal.LotSize,
            ContractSize                  = contractSize,
            PipSize                       = pipSize,
            StopLoss                      = signal.StopLoss,
            TakeProfit                    = signal.TakeProfit,
            Status                        = PaperExecutionStatus.Open,
            TcaProfileSnapshotJson        = JsonSerializer.Serialize(profile),
        };

        await db.Set<PaperExecution>().AddAsync(row, ct);
        await _writeCtx.SaveChangesAsync(ct);
    }
}
