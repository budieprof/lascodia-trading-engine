using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;

// ── Command ───────────────────────────────────────────────────────────────────

public class LogDecisionCommand : IRequest<ResponseData<long>>
{
    public required string EntityType   { get; set; }
    public long            EntityId     { get; set; }
    public required string DecisionType { get; set; }
    public required string Outcome      { get; set; }
    public required string Reason       { get; set; }
    public string?         ContextJson  { get; set; }
    public required string Source       { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class LogDecisionCommandValidator : AbstractValidator<LogDecisionCommand>
{
    public LogDecisionCommandValidator()
    {
        RuleFor(x => x.EntityType)
            .NotEmpty().WithMessage("EntityType cannot be empty")
            .MaximumLength(50).WithMessage("EntityType cannot exceed 50 characters");

        RuleFor(x => x.EntityId)
            .GreaterThan(0).WithMessage("EntityId must be greater than zero");

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
