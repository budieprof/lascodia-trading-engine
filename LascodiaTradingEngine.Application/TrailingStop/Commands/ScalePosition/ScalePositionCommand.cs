using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TrailingStop.Commands.ScalePosition;

// ── Command ───────────────────────────────────────────────────────────────────

public class ScalePositionCommand : IRequest<ResponseData<long>>
{
    public long    PositionId { get; set; }
    public string  ScaleType  { get; set; } = string.Empty;  // "In" | "Out"
    public decimal Lots       { get; set; }
    public decimal Price      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ScalePositionCommandValidator : AbstractValidator<ScalePositionCommand>
{
    public ScalePositionCommandValidator()
    {
        RuleFor(x => x.PositionId)
            .GreaterThan(0).WithMessage("PositionId must be greater than zero");

        RuleFor(x => x.Lots)
            .GreaterThan(0).WithMessage("Lots must be greater than zero");

        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ScalePositionCommandHandler : IRequestHandler<ScalePositionCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public ScalePositionCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(ScalePositionCommand request, CancellationToken cancellationToken)
    {
        var position = await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .FirstOrDefaultAsync(x => x.Id == request.PositionId && !x.IsDeleted, cancellationToken);

        if (position is null)
            return ResponseData<long>.Init(0, false, "Position not found", "-14");

        var scaleOrder = new Domain.Entities.PositionScaleOrder
        {
            PositionId = request.PositionId,
            ScaleType  = request.ScaleType == "In" ? ScaleType.ScaleIn : ScaleType.ScaleOut,
            LotSize    = request.Lots,
            Status     = ScaleOrderStatus.Pending
        };

        if (request.ScaleType == "In")
        {
            var totalLots         = position.OpenLots + request.Lots;
            position.AverageEntryPrice = totalLots > 0
                ? (position.OpenLots * position.AverageEntryPrice + request.Lots * request.Price) / totalLots
                : request.Price;
            position.OpenLots += request.Lots;
        }
        else
        {
            position.OpenLots = Math.Max(0, position.OpenLots - request.Lots);
        }

        await _context.GetDbContext()
            .Set<Domain.Entities.PositionScaleOrder>()
            .AddAsync(scaleOrder, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(scaleOrder.Id, true, "Successful", "00");
    }
}
