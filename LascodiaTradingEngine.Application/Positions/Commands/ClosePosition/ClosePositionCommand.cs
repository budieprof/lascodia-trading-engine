using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Commands.ClosePosition;

// ── Command ───────────────────────────────────────────────────────────────────

public class ClosePositionCommand : IRequest<ResponseData<string>>
{
    public long    Id        { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal? CloseLots { get; set; }  // defaults to all open lots
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ClosePositionCommandValidator : AbstractValidator<ClosePositionCommand>
{
    public ClosePositionCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.ClosePrice).GreaterThan(0);
        RuleFor(x => x.CloseLots).GreaterThan(0).When(x => x.CloseLots.HasValue);
    }
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
        var dbContext = _context.GetDbContext();

        var entity = await dbContext
            .Set<Domain.Entities.Position>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.Status == PositionStatus.Open && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Open position not found", "-14");

        decimal closeLots = request.CloseLots ?? entity.OpenLots;

        if (closeLots > entity.OpenLots)
            return ResponseData<string>.Init(null, false, "CloseLots cannot exceed OpenLots", "-11");

        // Load the actual contract size from the currency pair spec instead of assuming 100k
        var currencyPair = await dbContext
            .Set<Domain.Entities.CurrencyPair>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == entity.Symbol && !x.IsDeleted, cancellationToken);

        decimal contractSize = currencyPair?.ContractSize ?? 100_000m;

        decimal realizedForClose = entity.Direction == PositionDirection.Long
            ? (request.ClosePrice - entity.AverageEntryPrice) * closeLots * contractSize
            : (entity.AverageEntryPrice - request.ClosePrice) * closeLots * contractSize;

        // Wrap position update + EA command queuing in an explicit transaction
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        entity.RealizedPnL += realizedForClose;

        // Include swap and commission in realized P&L only on full close
        // (partial closes leave swap/commission on the remaining position)
        if (entity.OpenLots - closeLots == 0)
            entity.RealizedPnL += entity.Swap + entity.Commission;
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

        // Queue an EACommand so the EA closes the position on MT5.
        // MT5 may have already closed it (SL/TP hit broker-side), in which case
        // the EA will acknowledge the command as a no-op.
        if (!string.IsNullOrEmpty(entity.BrokerPositionId))
        {
            var eaInstance = await dbContext
                .Set<Domain.Entities.EAInstance>()
                .ActiveForSymbol(entity.Symbol)
                .FirstOrDefaultAsync(cancellationToken);

            if (eaInstance is not null)
            {
                await dbContext.Set<Domain.Entities.EACommand>().AddAsync(new Domain.Entities.EACommand
                {
                    TargetInstanceId = eaInstance.InstanceId,
                    CommandType      = EACommandType.ClosePosition,
                    TargetTicket     = long.TryParse(entity.BrokerPositionId, out var ticket) ? ticket : null,
                    Symbol           = entity.Symbol,
                }, cancellationToken);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ResponseData<string>.Init("Closed", true, "Successful", "00");
    }
}
