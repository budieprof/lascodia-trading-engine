using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Orders.Commands.ModifyOrder;

public class ModifyOrderCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore]
    public long Id { get; set; }
    public decimal? StopLoss   { get; set; }
    public decimal? TakeProfit { get; set; }
}

public class ModifyOrderCommandHandler : IRequestHandler<ModifyOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IBrokerOrderExecutor _broker;

    public ModifyOrderCommandHandler(IWriteApplicationDbContext context, IBrokerOrderExecutor broker)
    {
        _context = context;
        _broker  = broker;
    }

    public async Task<ResponseData<string>> Handle(ModifyOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (order is null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        if (!string.IsNullOrEmpty(order.BrokerOrderId))
        {
            var result = await _broker.ModifyOrderAsync(order.BrokerOrderId, request.StopLoss, request.TakeProfit, cancellationToken);
            if (!result.Success)
                return ResponseData<string>.Init(null, false, result.ErrorMessage ?? "Broker modify failed", "-11");
        }

        order.StopLoss   = request.StopLoss   ?? order.StopLoss;
        order.TakeProfit = request.TakeProfit  ?? order.TakeProfit;

        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
