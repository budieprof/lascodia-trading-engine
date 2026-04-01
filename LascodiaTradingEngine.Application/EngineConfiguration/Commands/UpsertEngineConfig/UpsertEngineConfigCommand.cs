using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
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
    private readonly IApprovalWorkflow _approvalWorkflow;
    private readonly ICurrentUserService _currentUser;

    private static readonly string[] RiskRelatedKeywords = ["Risk", "Max", "Limit", "VaR", "Drawdown"];

    public UpsertEngineConfigCommandHandler(
        IWriteApplicationDbContext context,
        IMediator mediator,
        IApprovalWorkflow approvalWorkflow,
        ICurrentUserService currentUser)
    {
        _context = context;
        _mediator = mediator;
        _approvalWorkflow = approvalWorkflow;
        _currentUser = currentUser;
    }

    public async Task<ResponseData<long>> Handle(UpsertEngineConfigCommand request, CancellationToken cancellationToken)
    {
        var existing = await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == request.Key && !x.IsDeleted, cancellationToken);

        var dataType = Enum.Parse<ConfigDataType>(request.DataType, ignoreCase: true);
        long currentAccountId = long.TryParse(_currentUser.UserId, out var parsedUid) ? parsedUid : 0;

        // ── Four-eyes approval gate (risk-related config keys only) ──
        bool isRiskRelatedKey = RiskRelatedKeywords.Any(kw =>
            request.Key.Contains(kw, StringComparison.OrdinalIgnoreCase));

        if (isRiskRelatedKey)
        {
            // Use a stable hash of the config key as the target entity ID so that
            // new keys (where existing is null) don't all collapse to ID 0.
            long targetEntityId = existing?.Id ?? ComputeStableKeyId(request.Key);
            if (!await _approvalWorkflow.IsApprovedAsync(ApprovalOperationType.ConfigChange, targetEntityId, cancellationToken))
            {
                await _approvalWorkflow.RequestApprovalAsync(
                    ApprovalOperationType.ConfigChange,
                    targetEntityId,
                    "EngineConfig",
                    $"Update risk-related config '{request.Key}' to '{request.Value}'",
                    System.Text.Json.JsonSerializer.Serialize(new { request.Key, request.Value }),
                    currentAccountId,
                    cancellationToken);
                return ResponseData<long>.Init(0, false, "Pending four-eyes approval", "-202");
            }

            // Consume the approval so it cannot be re-used for a subsequent change
            if (!await _approvalWorkflow.ConsumeApprovalAsync(ApprovalOperationType.ConfigChange, targetEntityId, cancellationToken))
                return ResponseData<long>.Init(0, false, "Approval was already consumed by a concurrent request", "-409");
        }

        if (existing is not null)
        {
            // Capture before-state for audit trail
            var previousValue = existing.Value;

            existing.Value           = request.Value;
            existing.Description     = request.Description;
            existing.DataType        = dataType;
            existing.IsHotReloadable = request.IsHotReloadable;
            existing.LastUpdatedAt   = DateTime.UtcNow;

            // ── Persist EngineConfigAuditLog (same SaveChanges for atomicity) ──
            var auditLog = new EngineConfigAuditLog
            {
                Key = request.Key,
                OldValue = previousValue,
                NewValue = request.Value,
                ChangedByAccountId = currentAccountId,
                Reason = "Configuration update",
                ChangedAt = DateTime.UtcNow
            };
            await _context.GetDbContext().Set<EngineConfigAuditLog>().AddAsync(auditLog, cancellationToken);

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

        // ── Persist EngineConfigAuditLog for new key (same SaveChanges for atomicity) ──
        var createAuditLog = new EngineConfigAuditLog
        {
            Key = request.Key,
            OldValue = null,
            NewValue = request.Value,
            ChangedByAccountId = currentAccountId,
            Reason = "Configuration created",
            ChangedAt = DateTime.UtcNow
        };
        await _context.GetDbContext().Set<EngineConfigAuditLog>().AddAsync(createAuditLog, cancellationToken);

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

    /// <summary>
    /// Produces a stable negative ID from the config key so that approval requests for
    /// not-yet-persisted keys are uniquely identified without colliding with real entity IDs.
    /// Uses a deterministic hash (FNV-1a) because string.GetHashCode() is randomized per-process in .NET Core.
    /// </summary>
    private static long ComputeStableKeyId(string key)
    {
        // FNV-1a 64-bit hash — deterministic across process restarts.
        // Note: operates on UTF-16 chars (2 bytes each) rather than byte-level FNV.
        // This is fine for ASCII config keys; non-ASCII keys will still produce stable
        // hashes but won't match reference byte-level FNV implementations.
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime  = 1099511628211UL;

        ulong hash = fnvOffset;
        foreach (char c in key.ToUpperInvariant())
        {
            hash ^= c;
            hash *= fnvPrime;
        }

        // Return as negative long to avoid collision with real auto-increment entity IDs
        return -(long)(hash & 0x7FFFFFFFFFFFFFFFUL) - 1; // Always negative, never 0
    }
}
