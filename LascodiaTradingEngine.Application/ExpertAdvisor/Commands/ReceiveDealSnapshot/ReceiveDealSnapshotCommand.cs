using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveDealSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives a snapshot of recent deals (filled trades) from the EA.
/// Used to update order fill status and record execution details.
/// </summary>
public class ReceiveDealSnapshotCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance providing the deal snapshot.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of recent deals (filled trades) to process.</summary>
    public List<DealSnapshotItem> Deals { get; set; } = new();
}

/// <summary>
/// Represents a single completed deal (trade fill) from MetaTrader 5's deal history.
/// </summary>
public class DealSnapshotItem
{
    /// <summary>Broker-assigned deal ticket number.</summary>
    public long     Ticket        { get; set; }

    /// <summary>Ticket of the order that generated this deal.</summary>
    public long     OrderTicket   { get; set; }

    /// <summary>Ticket of the position this deal belongs to.</summary>
    public long     PositionTicket { get; set; }

    /// <summary>Instrument symbol.</summary>
    public required string Symbol { get; set; }

    /// <summary>Deal direction: "Buy" or "Sell".</summary>
    public required string DealType { get; set; }

    /// <summary>Filled volume in lots.</summary>
    public decimal  Volume        { get; set; }

    /// <summary>Execution price of the deal.</summary>
    public decimal  Price         { get; set; }

    /// <summary>Realised profit/loss from this deal.</summary>
    public decimal  Profit        { get; set; }

    /// <summary>Broker commission charged for this deal.</summary>
    public decimal  Commission    { get; set; }

    /// <summary>Swap charges applied to this deal.</summary>
    public decimal  Swap          { get; set; }

    /// <summary>UTC time when the deal was executed.</summary>
    public DateTime DealTime      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty and Deals is not null.
/// </summary>
public class ReceiveDealSnapshotCommandValidator : AbstractValidator<ReceiveDealSnapshotCommand>
{
    public ReceiveDealSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Deals)
            .NotNull().WithMessage("Deals cannot be null");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles deal snapshot processing. Matches each deal's OrderTicket to an existing engine Order
/// by BrokerOrderId. If the order is found and not yet filled, updates it with the fill price,
/// quantity, status (Filled), and fill timestamp from the deal.
/// </summary>
public class ReceiveDealSnapshotCommandHandler : IRequestHandler<ReceiveDealSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ReceiveDealSnapshotCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ReceiveDealSnapshotCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        foreach (var deal in request.Deals)
        {
            var orderTicket = deal.OrderTicket.ToString();

            // Try to match to an existing engine order by broker order ID
            var order = await dbContext
                .Set<Domain.Entities.Order>()
                .FirstOrDefaultAsync(
                    x => x.BrokerOrderId == orderTicket && !x.IsDeleted,
                    cancellationToken);

            if (order is not null && order.Status != OrderStatus.Filled)
            {
                order.FilledPrice    = deal.Price;
                order.FilledQuantity = deal.Volume;
                order.Status         = OrderStatus.Filled;
                order.FilledAt       = deal.DealTime;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
