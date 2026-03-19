using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.DrawdownRecovery.Commands.RecordDrawdownSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

public class RecordDrawdownSnapshotCommand : IRequest<ResponseData<string>>
{
    public decimal CurrentEquity { get; set; }
    public decimal PeakEquity    { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RecordDrawdownSnapshotCommandValidator : AbstractValidator<RecordDrawdownSnapshotCommand>
{
    public RecordDrawdownSnapshotCommandValidator()
    {
        RuleFor(x => x.CurrentEquity)
            .GreaterThanOrEqualTo(0).WithMessage("CurrentEquity must be >= 0");

        RuleFor(x => x.PeakEquity)
            .GreaterThan(0).WithMessage("PeakEquity must be > 0");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RecordDrawdownSnapshotCommandHandler
    : IRequestHandler<RecordDrawdownSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RecordDrawdownSnapshotCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(
        RecordDrawdownSnapshotCommand request, CancellationToken cancellationToken)
    {
        decimal drawdownPct = (request.PeakEquity - request.CurrentEquity) / request.PeakEquity * 100m;

        RecoveryMode recoveryMode = drawdownPct >= 20m ? RecoveryMode.Halted
            : drawdownPct >= 10m ? RecoveryMode.Reduced
            : RecoveryMode.Normal;

        var snapshot = new Domain.Entities.DrawdownSnapshot
        {
            CurrentEquity = request.CurrentEquity,
            PeakEquity    = request.PeakEquity,
            DrawdownPct   = drawdownPct,
            RecoveryMode  = recoveryMode,
            RecordedAt    = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.DrawdownSnapshot>()
            .AddAsync(snapshot, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(recoveryMode.ToString(), true, "Successful", "00");
    }
}
