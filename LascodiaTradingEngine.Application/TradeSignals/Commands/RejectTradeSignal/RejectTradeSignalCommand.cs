using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Rejects a pending trade signal with an optional reason, preventing it from being executed.</summary>
public class RejectTradeSignalCommand : IRequest<ResponseData<string>>
{
    /// <summary>Trade signal identifier to reject.</summary>
    public long   Id     { get; set; }
    /// <summary>Human-readable rejection reason.</summary>
    public string Reason { get; set; } = string.Empty;
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the trade signal Id is a positive value.</summary>
public class RejectTradeSignalCommandValidator : AbstractValidator<RejectTradeSignalCommand>
{
    public RejectTradeSignalCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Transitions the signal from Pending to Rejected and records the rejection reason.</summary>
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
