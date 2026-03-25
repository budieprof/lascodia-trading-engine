using FluentValidation;
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

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdatePositionPriceCommandValidator : AbstractValidator<UpdatePositionPriceCommand>
{
    public UpdatePositionPriceCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.CurrentPrice).GreaterThan(0);
    }
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

        // Load actual contract size from currency pair spec instead of assuming 100k
        var currencyPair = await _context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == entity.Symbol && !x.IsDeleted, cancellationToken);

        decimal contractSize = currencyPair?.ContractSize ?? 100_000m;

        entity.UnrealizedPnL = entity.Direction == PositionDirection.Long
            ? (request.CurrentPrice - entity.AverageEntryPrice) * entity.OpenLots * contractSize
            : (entity.AverageEntryPrice - request.CurrentPrice) * entity.OpenLots * contractSize;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
