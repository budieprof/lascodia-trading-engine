using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.CancelOrder;

public class CancelOrderCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

public class CancelOrderCommandHandler : IRequestHandler<CancelOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IBrokerOrderExecutor _broker;

    public CancelOrderCommandHandler(IWriteApplicationDbContext context, IBrokerOrderExecutor broker)
    {
        _context = context;
        _broker  = broker;
    }

    public async Task<ResponseData<string>> Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (order is null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        if (order.Status is not (OrderStatus.Pending or OrderStatus.Submitted or OrderStatus.PartialFill))
            return ResponseData<string>.Init(null, false, "Order cannot be cancelled in its current status", "-11");

        // If already submitted to broker, cancel at broker first
        if (!string.IsNullOrEmpty(order.BrokerOrderId))
        {
            var result = await _broker.CancelOrderAsync(order.BrokerOrderId, cancellationToken);
            if (!result.Success)
                return ResponseData<string>.Init(null, false, result.ErrorMessage ?? "Broker cancel failed", "-11");
        }

        order.Status = OrderStatus.Cancelled;
        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
