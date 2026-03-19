using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Commands.ClosePosition;

// ── Command ───────────────────────────────────────────────────────────────────

public class ClosePositionCommand : IRequest<ResponseData<string>>
{
    public long    Id        { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal? CloseLots { get; set; }  // defaults to all open lots
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ClosePositionCommandHandler : IRequestHandler<ClosePositionCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ClosePositionCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ClosePositionCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.Status == PositionStatus.Open && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Open position not found", "-14");

        decimal closeLots = request.CloseLots ?? entity.OpenLots;

        if (closeLots > entity.OpenLots)
            return ResponseData<string>.Init(null, false, "CloseLots cannot exceed OpenLots", "-11");

        const decimal standardLot = 100_000m;

        decimal realizedForClose = entity.Direction == PositionDirection.Long
            ? (request.ClosePrice - entity.AverageEntryPrice) * closeLots * standardLot
            : (entity.AverageEntryPrice - request.ClosePrice) * closeLots * standardLot;

        entity.RealizedPnL += realizedForClose;
        entity.OpenLots    -= closeLots;

        if (entity.OpenLots == 0)
        {
            entity.Status   = PositionStatus.Closed;
            entity.ClosedAt = DateTime.UtcNow;
        }
        else
        {
            entity.Status = PositionStatus.Open;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Closed", true, "Successful", "00");
    }
}
