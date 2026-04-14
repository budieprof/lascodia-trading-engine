using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Records an immutable decision log entry for audit trail purposes. Used by workers and
/// handlers to capture the reasoning behind automated and manual decisions.
/// </summary>
public class LogDecisionCommand : IRequest<ResponseData<long>>
{
    /// <summary>The type of entity the decision pertains to (e.g., "Order", "RiskProfile").</summary>
    public required string EntityType   { get; set; }
    /// <summary>The ID of the entity involved in the decision. Use 0 for pipeline-level events that have no entity yet (e.g. screening failures before a Strategy row is created).</summary>
    public long            EntityId     { get; set; }
    /// <summary>The category of decision made (e.g., "SignalApproved", "ConfigUpdated").</summary>
    public required string DecisionType { get; set; }
    /// <summary>The result of the decision (e.g., "Approved", "Rejected", "Updated").</summary>
    public required string Outcome      { get; set; }
    /// <summary>Human-readable explanation of why this decision was made.</summary>
    public required string Reason       { get; set; }
    /// <summary>Optional JSON payload with before/after state or additional context.</summary>
    public string?         ContextJson  { get; set; }
    /// <summary>The originating component (e.g., command name, worker name).</summary>
    public required string Source       { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates required fields and length constraints for the decision log entry.</summary>
public class LogDecisionCommandValidator : AbstractValidator<LogDecisionCommand>
{
    public LogDecisionCommandValidator()
    {
        RuleFor(x => x.EntityType)
            .NotEmpty().WithMessage("EntityType cannot be empty")
            .MaximumLength(50).WithMessage("EntityType cannot exceed 50 characters");

        RuleFor(x => x.EntityId)
            .GreaterThanOrEqualTo(0).WithMessage("EntityId cannot be negative");

        RuleFor(x => x.DecisionType)
            .NotEmpty().WithMessage("DecisionType cannot be empty")
            .MaximumLength(50).WithMessage("DecisionType cannot exceed 50 characters");

        RuleFor(x => x.Outcome)
            .NotEmpty().WithMessage("Outcome cannot be empty")
            .MaximumLength(50).WithMessage("Outcome cannot exceed 50 characters");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason cannot be empty");

        RuleFor(x => x.Source)
            .NotEmpty().WithMessage("Source cannot be empty")
            .MaximumLength(50).WithMessage("Source cannot exceed 50 characters");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Persists an immutable <see cref="Domain.Entities.DecisionLog"/> entry with a UTC timestamp.
/// Decision logs are append-only and never soft-deleted.
/// </summary>
public class LogDecisionCommandHandler : IRequestHandler<LogDecisionCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public LogDecisionCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(LogDecisionCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.DecisionLog
        {
            EntityType   = request.EntityType,
            EntityId     = request.EntityId,
            DecisionType = request.DecisionType,
            Outcome      = request.Outcome,
            Reason       = request.Reason,
            ContextJson  = request.ContextJson,
            Source       = request.Source,
            CreatedAt    = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.DecisionLog>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
