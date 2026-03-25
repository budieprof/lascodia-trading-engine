using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceivePositionSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

public class ReceivePositionSnapshotCommand : IRequest<ResponseData<string>>
{
    public required string InstanceId { get; set; }
    public List<PositionSnapshotItem> Positions { get; set; } = new();
}

public class PositionSnapshotItem
{
    public long     Ticket          { get; set; }
    public required string Symbol   { get; set; }
    public required string Direction { get; set; }  // "Long" | "Short"
    public decimal  Volume          { get; set; }
    public decimal  OpenPrice       { get; set; }
    public decimal  CurrentPrice    { get; set; }
    public decimal? StopLoss        { get; set; }
    public decimal? TakeProfit      { get; set; }
    public decimal  Profit          { get; set; }
    public decimal  Swap            { get; set; }
    public decimal  Commission      { get; set; }
    public DateTime OpenTime        { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ReceivePositionSnapshotCommandValidator : AbstractValidator<ReceivePositionSnapshotCommand>
{
    public ReceivePositionSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Positions)
            .NotNull().WithMessage("Positions cannot be null")
            .Must(p => p.Count <= 500).WithMessage("Position snapshot cannot exceed 500 items");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ReceivePositionSnapshotCommandHandler : IRequestHandler<ReceivePositionSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceivePositionSnapshotCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ReceivePositionSnapshotCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var dbContext = _context.GetDbContext();

        foreach (var snap in request.Positions)
        {
            var brokerTicket = snap.Ticket.ToString();
            var symbol = snap.Symbol.ToUpperInvariant();

            var existing = await dbContext
                .Set<Domain.Entities.Position>()
                .FirstOrDefaultAsync(
                    x => x.BrokerPositionId == brokerTicket
                      && x.Symbol == symbol
                      && !x.IsDeleted,
                    cancellationToken);

            if (existing is not null)
            {
                existing.CurrentPrice  = snap.CurrentPrice;
                existing.UnrealizedPnL = snap.Profit;
                existing.Swap          = snap.Swap;
                existing.Commission    = snap.Commission;
                existing.StopLoss      = snap.StopLoss;
                existing.TakeProfit    = snap.TakeProfit;
                existing.OpenLots      = snap.Volume;
            }
            else
            {
                var direction = Enum.Parse<PositionDirection>(snap.Direction, ignoreCase: true);

                await dbContext.Set<Domain.Entities.Position>().AddAsync(new Domain.Entities.Position
                {
                    Symbol            = symbol,
                    Direction         = direction,
                    OpenLots          = snap.Volume,
                    AverageEntryPrice = snap.OpenPrice,
                    CurrentPrice      = snap.CurrentPrice,
                    UnrealizedPnL     = snap.Profit,
                    Swap              = snap.Swap,
                    Commission        = snap.Commission,
                    StopLoss          = snap.StopLoss,
                    TakeProfit        = snap.TakeProfit,
                    Status            = PositionStatus.Open,
                    BrokerPositionId  = brokerTicket,
                    OpenedAt          = snap.OpenTime,
                }, cancellationToken);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
