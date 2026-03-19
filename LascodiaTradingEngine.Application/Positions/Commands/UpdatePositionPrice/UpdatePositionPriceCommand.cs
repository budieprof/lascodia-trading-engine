using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Commands.UpdatePositionPrice;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdatePositionPriceCommand : IRequest<ResponseData<string>>
{
    public long    Id           { get; set; }
    public decimal CurrentPrice { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdatePositionPriceCommandHandler : IRequestHandler<UpdatePositionPriceCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdatePositionPriceCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdatePositionPriceCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.Status == PositionStatus.Open && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Open position not found", "-14");

        entity.CurrentPrice = request.CurrentPrice;

        const decimal standardLot = 100_000m;

        entity.UnrealizedPnL = entity.Direction == PositionDirection.Long
            ? (request.CurrentPrice - entity.AverageEntryPrice) * entity.OpenLots * standardLot
            : (entity.AverageEntryPrice - request.CurrentPrice) * entity.OpenLots * standardLot;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
