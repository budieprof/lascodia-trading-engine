using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;

// ── Command ───────────────────────────────────────────────────────────────────

public class RejectTradeSignalCommand : IRequest<ResponseData<string>>
{
    public long   Id     { get; set; }
    public string Reason { get; set; } = string.Empty;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RejectTradeSignalCommandValidator : AbstractValidator<RejectTradeSignalCommand>
{
    public RejectTradeSignalCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RejectTradeSignalCommandHandler : IRequestHandler<RejectTradeSignalCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RejectTradeSignalCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(RejectTradeSignalCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradeSignal>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Trade signal not found", "-14");

        if (entity.Status != TradeSignalStatus.Pending)
            return ResponseData<string>.Init(null, false, "Signal is not in Pending status", "-11");

        entity.Status          = TradeSignalStatus.Rejected;
        entity.RejectionReason = request.Reason;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Rejected", true, "Successful", "00");
    }
}
