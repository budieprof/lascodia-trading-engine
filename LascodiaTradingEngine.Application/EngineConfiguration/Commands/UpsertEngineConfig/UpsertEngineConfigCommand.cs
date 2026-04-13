using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EngineConfiguration.Commands.UpsertEngineConfig;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates or updates an engine configuration key-value pair. All changes are audit-logged.
/// </summary>
public class UpsertEngineConfigCommand : IRequest<ResponseData<long>>
{
    public string  Key              { get; set; } = string.Empty;
    public string  Value            { get; set; } = string.Empty;
    public string? Description      { get; set; }
    public string  DataType         { get; set; } = "String";
    public bool    IsHotReloadable  { get; set; } = true;
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates key, value, and data type for the engine configuration upsert.</summary>
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

/// <summary>
/// Upserts an engine config entry. Creates an <see cref="Domain.Entities.EngineConfigAuditLog"/>
/// and a <see cref="Domain.Entities.DecisionLog"/> entry for every change.
/// </summary>
public class UpsertEngineConfigCommandHandler : IRequestHandler<UpsertEngineConfigCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IMediator _mediator;
    private readonly ICurrentUserService _currentUser;
    private readonly OptimizationConfigProvider _optimizationConfigProvider;
    private readonly TimeProvider _timeProvider;

    public UpsertEngineConfigCommandHandler(
        IWriteApplicationDbContext context,
        IMediator mediator,
        ICurrentUserService currentUser,
        OptimizationConfigProvider optimizationConfigProvider,
        TimeProvider timeProvider)
    {
        _context = context;
        _mediator = mediator;
        _currentUser = currentUser;
        _optimizationConfigProvider = optimizationConfigProvider;
        _timeProvider = timeProvider;
    }

    public async Task<ResponseData<long>> Handle(UpsertEngineConfigCommand request, CancellationToken cancellationToken)
    {
        var existing = await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == request.Key && !x.IsDeleted, cancellationToken);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var dataType = Enum.Parse<ConfigDataType>(request.DataType, ignoreCase: true);
        long currentAccountId = long.TryParse(_currentUser.UserId, out var parsedUid) ? parsedUid : 0;

        if (existing is not null)
        {
            // Capture before-state for audit trail
            var previousValue = existing.Value;

            existing.Value           = request.Value;
            existing.Description     = request.Description;
            existing.DataType        = dataType;
            existing.IsHotReloadable = request.IsHotReloadable;
            existing.LastUpdatedAt   = nowUtc;

            // ── Persist EngineConfigAuditLog (same SaveChanges for atomicity) ──
            var auditLog = new EngineConfigAuditLog
            {
                Key = request.Key,
                OldValue = previousValue,
                NewValue = request.Value,
                ChangedByAccountId = currentAccountId,
                Reason = "Configuration update",
                ChangedAt = nowUtc
            };
            await _context.GetDbContext().Set<EngineConfigAuditLog>().AddAsync(auditLog, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            InvalidateOptimizationConfigCacheIfNeeded(request.Key);

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
            LastUpdatedAt   = nowUtc
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .AddAsync(entity, cancellationToken);

        // ── Persist EngineConfigAuditLog for new key (same SaveChanges for atomicity) ──
        var createAuditLog = new EngineConfigAuditLog
        {
            Key = request.Key,
            OldValue = null,
            NewValue = request.Value,
            ChangedByAccountId = currentAccountId,
            Reason = "Configuration created",
            ChangedAt = nowUtc
        };
        await _context.GetDbContext().Set<EngineConfigAuditLog>().AddAsync(createAuditLog, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        InvalidateOptimizationConfigCacheIfNeeded(request.Key);

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

    private void InvalidateOptimizationConfigCacheIfNeeded(string key)
    {
        if (key.StartsWith("Optimization:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Backtest:Gate:", StringComparison.OrdinalIgnoreCase))
        {
            _optimizationConfigProvider.InvalidateCache();
        }
    }

}
