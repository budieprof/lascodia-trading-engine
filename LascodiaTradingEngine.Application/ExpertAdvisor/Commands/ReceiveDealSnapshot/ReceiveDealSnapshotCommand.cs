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
    public required string InstanceId { get; set; }
    public List<DealSnapshotItem> Deals { get; set; } = new();
}

public class DealSnapshotItem
{
    public long     Ticket        { get; set; }
    public long     OrderTicket   { get; set; }
    public long     PositionTicket { get; set; }
    public required string Symbol { get; set; }
    public required string DealType { get; set; }  // "Buy" | "Sell"
    public decimal  Volume        { get; set; }
    public decimal  Price         { get; set; }
    public decimal  Profit        { get; set; }
    public decimal  Commission    { get; set; }
    public decimal  Swap          { get; set; }
    public DateTime DealTime      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
