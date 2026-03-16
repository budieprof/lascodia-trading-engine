using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Orders.Commands.DeleteOrder;

// ── Command ───────────────────────────────────────────────────────────────────

public class DeleteOrderCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore] public long Id { get; set; }
    [JsonIgnore] public int BusinessId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class DeleteOrderCommandHandler : IRequestHandler<DeleteOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventService;

    public DeleteOrderCommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService eventService)
    {
        _context = context;
        _eventService = eventService;
    }

    public async Task<ResponseData<string>> Handle(DeleteOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.BusinessId == request.BusinessId, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        entity.IsDeleted = true;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
