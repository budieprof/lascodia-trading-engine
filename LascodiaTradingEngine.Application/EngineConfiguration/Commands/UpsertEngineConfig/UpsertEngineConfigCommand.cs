using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EngineConfiguration.Commands.UpsertEngineConfig;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpsertEngineConfigCommand : IRequest<ResponseData<long>>
{
    public string  Key              { get; set; } = string.Empty;
    public string  Value            { get; set; } = string.Empty;
    public string? Description      { get; set; }
    public string  DataType         { get; set; } = "String";
    public bool    IsHotReloadable  { get; set; } = true;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpsertEngineConfigCommandValidator : AbstractValidator<UpsertEngineConfigCommand>
{
    public UpsertEngineConfigCommandValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("Key is required");

        RuleFor(x => x.Value)
            .NotEmpty().WithMessage("Value is required");

        RuleFor(x => x.DataType)
            .NotEmpty().WithMessage("DataType is required")
            .Must(t => Enum.TryParse<ConfigDataType>(t, ignoreCase: true, out _))
            .WithMessage("DataType must be 'String', 'Int', 'Decimal', 'Bool', or 'Json'");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpsertEngineConfigCommandHandler : IRequestHandler<UpsertEngineConfigCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IMediator _mediator;

    public UpsertEngineConfigCommandHandler(IWriteApplicationDbContext context, IMediator mediator)
    {
        _context = context;
        _mediator = mediator;
    }

    public async Task<ResponseData<long>> Handle(UpsertEngineConfigCommand request, CancellationToken cancellationToken)
    {
        var existing = await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == request.Key && !x.IsDeleted, cancellationToken);

        var dataType = Enum.Parse<ConfigDataType>(request.DataType, ignoreCase: true);

        if (existing is not null)
        {
            // Capture before-state for audit trail
            var previousValue = existing.Value;

            existing.Value           = request.Value;
            existing.Description     = request.Description;
            existing.DataType        = dataType;
            existing.IsHotReloadable = request.IsHotReloadable;
            existing.LastUpdatedAt   = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            await _mediator.Send(new LogDecisionCommand
            {
                EntityType   = "EngineConfig",
                EntityId     = existing.Id,
                DecisionType = "ConfigUpdated",
                Outcome      = "Updated",
                Reason       = $"Configuration '{request.Key}' updated",
                ContextJson  = AuditSnapshot.Capture(
                    before: new { Key = request.Key, Value = previousValue },
                    after:  new { Key = request.Key, Value = request.Value }),
                Source       = "UpsertEngineConfigCommand"
            }, cancellationToken);

            return ResponseData<long>.Init(existing.Id, true, "Updated", "00");
        }

        var entity = new Domain.Entities.EngineConfig
        {
            Key             = request.Key,
            Value           = request.Value,
            Description     = request.Description,
            DataType        = dataType,
            IsHotReloadable = request.IsHotReloadable,
            LastUpdatedAt   = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        await _mediator.Send(new LogDecisionCommand
        {
            EntityType   = "EngineConfig",
            EntityId     = entity.Id,
            DecisionType = "ConfigCreated",
            Outcome      = "Created",
            Reason       = $"Configuration '{request.Key}' created with value '{request.Value}'",
            ContextJson  = AuditSnapshot.CaptureCreated(
                new { request.Key, request.Value, request.DataType, request.IsHotReloadable }),
            Source       = "UpsertEngineConfigCommand"
        }, cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Created", "00");
    }
}
