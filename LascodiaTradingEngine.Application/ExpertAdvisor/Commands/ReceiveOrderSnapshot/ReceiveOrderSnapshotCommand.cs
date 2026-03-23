using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveOrderSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

public class ReceiveOrderSnapshotCommand : IRequest<ResponseData<string>>
{
    public required string InstanceId { get; set; }
    public List<OrderSnapshotItem> Orders { get; set; } = new();
}

public class OrderSnapshotItem
{
    public long     Ticket        { get; set; }
    public required string Symbol { get; set; }
    public required string OrderType  { get; set; }  // "Buy" | "Sell"
    public required string ExecutionType { get; set; }  // "Limit" | "Stop" | "StopLimit"
    public decimal  Volume        { get; set; }
    public decimal  Price         { get; set; }
    public decimal? StopLoss      { get; set; }
    public decimal? TakeProfit    { get; set; }
    public DateTime PlacedTime    { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ReceiveOrderSnapshotCommandValidator : AbstractValidator<ReceiveOrderSnapshotCommand>
{
    public ReceiveOrderSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Orders)
            .NotNull().WithMessage("Orders cannot be null");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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
