using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Commands.ApproveTradeSignal;

// ── Command ───────────────────────────────────────────────────────────────────

public class ApproveTradeSignalCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ApproveTradeSignalCommandHandler : IRequestHandler<ApproveTradeSignalCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ApproveTradeSignalCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ApproveTradeSignalCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradeSignal>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Trade signal not found", "-14");

        if (entity.Status != TradeSignalStatus.Pending)
            return ResponseData<string>.Init(null, false, "Signal is not in Pending status", "-11");

        entity.Status = TradeSignalStatus.Approved;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Approved", true, "Successful", "00");
    }
}
