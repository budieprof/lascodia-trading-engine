using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.RiskProfiles.Commands.UpdateRiskProfile;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateRiskProfileCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore] public long Id { get; set; }

    public required string Name                         { get; set; }
    public decimal         MaxLotSizePerTrade           { get; set; }
    public decimal         MaxDailyDrawdownPct          { get; set; }
    public decimal         MaxTotalDrawdownPct          { get; set; }
    public int             MaxOpenPositions             { get; set; }
    public int             MaxDailyTrades               { get; set; }
    public decimal         MaxRiskPerTradePct           { get; set; }
    public decimal         MaxSymbolExposurePct         { get; set; }
    public bool            IsDefault                    { get; set; }
    public decimal         DrawdownRecoveryThresholdPct { get; set; }
    public decimal         RecoveryLotSizeMultiplier    { get; set; }
    public decimal         RecoveryExitThresholdPct     { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateRiskProfileCommandValidator : AbstractValidator<UpdateRiskProfileCommand>
{
    public UpdateRiskProfileCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("RiskProfile Id is required");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(100).WithMessage("Name cannot exceed 100 characters");

        RuleFor(x => x.MaxLotSizePerTrade)
            .GreaterThan(0).WithMessage("MaxLotSizePerTrade must be greater than zero");

        RuleFor(x => x.MaxDailyDrawdownPct)
            .GreaterThan(0).WithMessage("MaxDailyDrawdownPct must be greater than zero");

        RuleFor(x => x.MaxTotalDrawdownPct)
            .GreaterThan(0).WithMessage("MaxTotalDrawdownPct must be greater than zero");

        RuleFor(x => x.MaxOpenPositions)
            .GreaterThan(0).WithMessage("MaxOpenPositions must be greater than zero");

        RuleFor(x => x.MaxDailyTrades)
            .GreaterThan(0).WithMessage("MaxDailyTrades must be greater than zero");

        RuleFor(x => x.MaxRiskPerTradePct)
            .GreaterThan(0).WithMessage("MaxRiskPerTradePct must be greater than zero");

        RuleFor(x => x.MaxSymbolExposurePct)
            .GreaterThan(0).WithMessage("MaxSymbolExposurePct must be greater than zero");

        RuleFor(x => x.DrawdownRecoveryThresholdPct)
            .GreaterThan(0).WithMessage("DrawdownRecoveryThresholdPct must be greater than zero");

        RuleFor(x => x.RecoveryLotSizeMultiplier)
            .GreaterThan(0).WithMessage("RecoveryLotSizeMultiplier must be greater than zero");

        RuleFor(x => x.RecoveryExitThresholdPct)
            .GreaterThan(0).WithMessage("RecoveryExitThresholdPct must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateRiskProfileCommandHandler : IRequestHandler<UpdateRiskProfileCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IMediator _mediator;
    private readonly IApprovalWorkflow _approvalWorkflow;
    private readonly ICurrentUserService _currentUser;

    public UpdateRiskProfileCommandHandler(
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

    public async Task<ResponseData<string>> Handle(UpdateRiskProfileCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.RiskProfile>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Risk profile not found", "-14");

        // ── Four-eyes approval gate (only when loosening limits) ──
        bool isLoosenedRiskLimits =
            request.MaxLotSizePerTrade > entity.MaxLotSizePerTrade ||
            request.MaxDailyDrawdownPct > entity.MaxDailyDrawdownPct ||
            request.MaxRiskPerTradePct > entity.MaxRiskPerTradePct ||
            request.MaxSymbolExposurePct > entity.MaxSymbolExposurePct;

        long currentAccountId = long.TryParse(_currentUser.UserId, out var parsedUid) ? parsedUid : 0;

        if (isLoosenedRiskLimits &&
            !await _approvalWorkflow.IsApprovedAsync(ApprovalOperationType.RiskLimitLoosening, request.Id, cancellationToken))
        {
            await _approvalWorkflow.RequestApprovalAsync(
                ApprovalOperationType.RiskLimitLoosening,
                request.Id,
                "RiskProfile",
                $"Loosen risk limits on profile '{entity.Name}'",
                System.Text.Json.JsonSerializer.Serialize(new { request.Id }),
                currentAccountId,
                cancellationToken);
            return ResponseData<string>.Init(null, false, "Pending four-eyes approval", "-202");
        }

        if (isLoosenedRiskLimits &&
            !await _approvalWorkflow.ConsumeApprovalAsync(ApprovalOperationType.RiskLimitLoosening, request.Id, cancellationToken))
            return ResponseData<string>.Init(null, false, "Approval was already consumed by a concurrent request", "-409");

        // Capture before-state for audit trail
        var beforeState = new
        {
            entity.Name,
            entity.MaxLotSizePerTrade,
            entity.MaxDailyDrawdownPct,
            entity.MaxTotalDrawdownPct,
            entity.MaxOpenPositions,
            entity.MaxDailyTrades,
            entity.MaxRiskPerTradePct,
            entity.MaxSymbolExposurePct,
            entity.IsDefault,
            entity.DrawdownRecoveryThresholdPct,
            entity.RecoveryLotSizeMultiplier,
            entity.RecoveryExitThresholdPct,
        };

        if (request.IsDefault && !entity.IsDefault)
        {
            var existingDefault = await _context.GetDbContext()
                .Set<Domain.Entities.RiskProfile>()
                .FirstOrDefaultAsync(x => x.IsDefault && !x.IsDeleted && x.Id != request.Id, cancellationToken);

            if (existingDefault != null)
                existingDefault.IsDefault = false;
        }

        entity.Name                         = request.Name;
        entity.MaxLotSizePerTrade           = request.MaxLotSizePerTrade;
        entity.MaxDailyDrawdownPct          = request.MaxDailyDrawdownPct;
        entity.MaxTotalDrawdownPct          = request.MaxTotalDrawdownPct;
        entity.MaxOpenPositions             = request.MaxOpenPositions;
        entity.MaxDailyTrades               = request.MaxDailyTrades;
        entity.MaxRiskPerTradePct           = request.MaxRiskPerTradePct;
        entity.MaxSymbolExposurePct         = request.MaxSymbolExposurePct;
        entity.IsDefault                    = request.IsDefault;
        entity.DrawdownRecoveryThresholdPct = request.DrawdownRecoveryThresholdPct;
        entity.RecoveryLotSizeMultiplier    = request.RecoveryLotSizeMultiplier;
        entity.RecoveryExitThresholdPct     = request.RecoveryExitThresholdPct;

        await _context.SaveChangesAsync(cancellationToken);

        // Log before/after snapshot for compliance audit
        var afterState = new
        {
            request.Name,
            request.MaxLotSizePerTrade,
            request.MaxDailyDrawdownPct,
            request.MaxTotalDrawdownPct,
            request.MaxOpenPositions,
            request.MaxDailyTrades,
            request.MaxRiskPerTradePct,
            request.MaxSymbolExposurePct,
            request.IsDefault,
            request.DrawdownRecoveryThresholdPct,
            request.RecoveryLotSizeMultiplier,
            request.RecoveryExitThresholdPct,
        };

        await _mediator.Send(new LogDecisionCommand
        {
            EntityType   = "RiskProfile",
            EntityId     = entity.Id,
            DecisionType = "RiskProfileUpdated",
            Outcome      = "Updated",
            Reason       = $"Risk profile '{entity.Name}' updated",
            ContextJson  = AuditSnapshot.Capture(beforeState, afterState),
            Source       = "UpdateRiskProfileCommand"
        }, cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
