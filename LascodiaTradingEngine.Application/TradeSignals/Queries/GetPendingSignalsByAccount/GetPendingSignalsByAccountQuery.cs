using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Queries.GetPendingSignalsByAccount;

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>
/// A trade signal paired with the trading account that should execute it.
/// Returned by <see cref="GetPendingSignalsByAccountQuery"/> for bridge fan-out.
/// </summary>
public record AccountSignalItem(
    long   AccountId,
    long   SignalId,
    long   EngineOrderId,   // Order.Id (assigned by SignalOrderBridgeWorker)
    string Symbol,
    int    Direction,       // TradeDirection as int
    int    ExecutionType,   // 0 = Market (default); extended types map from OrderType
    double EntryPrice,
    double StopLoss,
    double TakeProfit,
    double LotSize,
    double Confidence,
    long   StrategyId,
    string StrategyName,
    long   ExpiresAtUnix,  // Unix seconds
    long   CreatedAtUnix); // Unix seconds (GeneratedAt)

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Returns signals that have been approved AND have an assigned Order (OrderId != null),
/// grouped by the owning TradingAccount so the bridge can fan them out to the correct
/// EA connections without broadcasting to unrelated accounts.
///
/// This is the bridge-specific counterpart to GetPendingExecutionTradeSignalsQuery
/// (which filters OrderId == null for the REST polling path).
/// </summary>
public class GetPendingSignalsByAccountQuery : IRequest<ResponseData<List<AccountSignalItem>>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Joins approved signals with their assigned orders and strategies to resolve the owning
/// TradingAccount, enabling the bridge to fan out signals to the correct EA connections.
/// </summary>
public class GetPendingSignalsByAccountQueryHandler
    : IRequestHandler<GetPendingSignalsByAccountQuery, ResponseData<List<AccountSignalItem>>>
{
    // Use write context (primary) instead of read replica to avoid lag-induced
    // duplicate or missed signal delivery. Signals are written by SignalOrderBridgeWorker
    // on the primary, and the bridge polls immediately after — a read replica may not
    // have replicated the new signal yet, causing missed or duplicate pushes.
    private readonly IWriteApplicationDbContext _context;

    public GetPendingSignalsByAccountQueryHandler(IWriteApplicationDbContext context)
        => _context = context;

    public async Task<ResponseData<List<AccountSignalItem>>> Handle(
        GetPendingSignalsByAccountQuery request,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var db  = _context.GetDbContext();

        // Join TradeSignal → Order to resolve TradingAccountId.
        // Only signals with an assigned order (OrderId != null) reach the bridge.
        var items = await (
            from signal in db.Set<Domain.Entities.TradeSignal>().AsNoTracking()
            where signal.Status == TradeSignalStatus.Approved
               && signal.OrderId != null
               && signal.ExpiresAt > now
               && !signal.IsDeleted
            join order in db.Set<Domain.Entities.Order>().AsNoTracking()
                on signal.OrderId equals order.Id
            join strategy in db.Set<Domain.Entities.Strategy>().AsNoTracking()
                on signal.StrategyId equals strategy.Id into strategyJoin
            from strategy in strategyJoin.DefaultIfEmpty()
            orderby signal.GeneratedAt
            select new AccountSignalItem(
                order.TradingAccountId,
                signal.Id,
                order.Id,
                signal.Symbol,
                (int)signal.Direction,
                0, // Market execution (bridge always sends market orders)
                (double)signal.EntryPrice,
                signal.StopLoss.HasValue ? (double)signal.StopLoss.Value : 0.0,
                signal.TakeProfit.HasValue ? (double)signal.TakeProfit.Value : 0.0,
                (double)signal.SuggestedLotSize,
                (double)signal.Confidence,
                signal.StrategyId,
                strategy != null ? strategy.Name : string.Empty,
                new DateTimeOffset(signal.ExpiresAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                new DateTimeOffset(signal.GeneratedAt, TimeSpan.Zero).ToUnixTimeSeconds())
            ).ToListAsync(cancellationToken);

        return ResponseData<List<AccountSignalItem>>.Init(items, true, "Successful", "00");
    }
}
