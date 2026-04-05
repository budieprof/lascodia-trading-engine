using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.DrawdownRecovery.Commands.RecordDrawdownSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Records a drawdown snapshot and computes the current recovery mode based on configured thresholds.
/// Used by the <c>DrawdownMonitorWorker</c> to track equity drawdowns in real time.
/// </summary>
public class RecordDrawdownSnapshotCommand : IRequest<ResponseData<string>>
{
    /// <summary>Current account equity value.</summary>
    public decimal CurrentEquity { get; set; }
    /// <summary>All-time peak equity (high-water mark).</summary>
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

/// <summary>
/// Calculates drawdown percentage from peak equity, determines the recovery mode
/// (Normal, Reduced, or Halted) based on risk thresholds, and persists the snapshot.
/// Returns the computed recovery mode name.
/// </summary>
public class RecordDrawdownSnapshotCommandHandler
    : IRequestHandler<RecordDrawdownSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly RiskCheckerOptions _options;

    public RecordDrawdownSnapshotCommandHandler(IWriteApplicationDbContext context, RiskCheckerOptions options)
    {
        _context = context;
        _options = options;
    }

    public async Task<ResponseData<string>> Handle(
        RecordDrawdownSnapshotCommand request, CancellationToken cancellationToken)
    {
        decimal drawdownPct = (request.PeakEquity - request.CurrentEquity) / request.PeakEquity * 100m;

        RecoveryMode recoveryMode = drawdownPct >= _options.HaltedDrawdownPct ? RecoveryMode.Halted
            : drawdownPct >= _options.ReducedDrawdownPct ? RecoveryMode.Reduced
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
