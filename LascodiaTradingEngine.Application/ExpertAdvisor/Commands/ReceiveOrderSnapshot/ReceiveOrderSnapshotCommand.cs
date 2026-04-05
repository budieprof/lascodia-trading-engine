using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveOrderSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives a snapshot of pending orders from the broker via an EA instance.
/// Upserts engine Order records by matching on BrokerOrderId + Symbol.
/// Called during EA startup and periodically to synchronise pending order state.
/// </summary>
public class ReceiveOrderSnapshotCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance providing the snapshot.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of pending orders as reported by the broker. Capped at 500 items.</summary>
    public List<OrderSnapshotItem> Orders { get; set; } = new();
}

/// <summary>
/// Represents a single pending order from the broker's perspective (MetaTrader 5 order data).
/// </summary>
public class OrderSnapshotItem
{
    /// <summary>Broker-assigned order ticket number.</summary>
    public long     Ticket        { get; set; }

    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public required string Symbol { get; set; }

    /// <summary>Order direction: "Buy" or "Sell".</summary>
    public required string OrderType  { get; set; }

    /// <summary>Execution method: "Limit", "Stop", or "StopLimit".</summary>
    public required string ExecutionType { get; set; }

    /// <summary>Order volume in lots.</summary>
    public decimal  Volume        { get; set; }

    /// <summary>Order trigger/limit price.</summary>
    public decimal  Price         { get; set; }

    /// <summary>Stop loss level attached to the order, if set.</summary>
    public decimal? StopLoss      { get; set; }

    /// <summary>Take profit level attached to the order, if set.</summary>
    public decimal? TakeProfit    { get; set; }

    /// <summary>UTC time when the order was placed on the broker.</summary>
    public DateTime PlacedTime    { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty, Orders is not null, and batch size does not exceed 500 items.
/// </summary>
public class ReceiveOrderSnapshotCommandValidator : AbstractValidator<ReceiveOrderSnapshotCommand>
{
    public ReceiveOrderSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Orders)
            .NotNull().WithMessage("Orders cannot be null")
            .Must(o => o.Count <= 500).WithMessage("Order snapshot cannot exceed 500 items");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles order snapshot processing. For each order: updates price, volume, SL/TP on the existing
/// engine Order if matched by BrokerOrderId + Symbol, or creates a new Order record with Submitted
/// status for unknown broker orders.
/// </summary>
public class ReceiveOrderSnapshotCommandHandler : IRequestHandler<ReceiveOrderSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ReceiveOrderSnapshotCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ReceiveOrderSnapshotCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        foreach (var snap in request.Orders)
        {
            var brokerTicket = snap.Ticket.ToString();
            var symbol = snap.Symbol.ToUpperInvariant();

            var existing = await dbContext
                .Set<Domain.Entities.Order>()
                .FirstOrDefaultAsync(
                    x => x.BrokerOrderId == brokerTicket
                      && x.Symbol == symbol
                      && !x.IsDeleted,
                    cancellationToken);

            if (existing is not null)
            {
                existing.Price      = snap.Price;
                existing.Quantity   = snap.Volume;
                existing.StopLoss   = snap.StopLoss;
                existing.TakeProfit = snap.TakeProfit;
            }
            else
            {
                var orderType     = Enum.Parse<OrderType>(snap.OrderType, ignoreCase: true);
                var executionType = Enum.Parse<ExecutionType>(snap.ExecutionType, ignoreCase: true);

                await dbContext.Set<Domain.Entities.Order>().AddAsync(new Domain.Entities.Order
                {
                    Symbol        = symbol,
                    OrderType     = orderType,
                    ExecutionType = executionType,
                    Quantity      = snap.Volume,
                    Price         = snap.Price,
                    StopLoss      = snap.StopLoss,
                    TakeProfit    = snap.TakeProfit,
                    Status        = OrderStatus.Submitted,
                    BrokerOrderId = brokerTicket,
                    CreatedAt     = snap.PlacedTime,
                }, cancellationToken);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
