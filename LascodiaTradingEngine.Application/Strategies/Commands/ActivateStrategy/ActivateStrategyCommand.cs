using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Commands.ActivateStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Activates a strategy that has reached the Approved lifecycle stage. Requires four-eyes
/// approval, queues an initial backtest run, and publishes a <see cref="StrategyActivatedIntegrationEvent"/>.
/// </summary>
public class ActivateStrategyCommand : IRequest<ResponseData<string>>
{
    /// <summary>Strategy identifier to activate.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the strategy Id is a positive value.</summary>
public class ActivateStrategyCommandValidator : AbstractValidator<ActivateStrategyCommand>
{
    public ActivateStrategyCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Enforces lifecycle stage and four-eyes approval gates, transitions the strategy to Active
/// status, queues a one-year initial backtest, and publishes a <see cref="StrategyActivatedIntegrationEvent"/>.
/// </summary>
public class ActivateStrategyCommandHandler : IRequestHandler<ActivateStrategyCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;
    private readonly IApprovalWorkflow _approvalWorkflow;

    private readonly ICurrentUserService _currentUser;

    public ActivateStrategyCommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService eventBus,
        IApprovalWorkflow approvalWorkflow,
        ICurrentUserService currentUser)
    {
        _context  = context;
        _eventBus = eventBus;
        _approvalWorkflow = approvalWorkflow;
        _currentUser = currentUser;
    }

    public async Task<ResponseData<string>> Handle(ActivateStrategyCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        // ── Lifecycle stage enforcement ──
        if (entity.LifecycleStage != StrategyLifecycleStage.Approved && entity.LifecycleStage != StrategyLifecycleStage.Active)
        {
            return ResponseData<string>.Init(null, false,
                $"Strategy must reach Approved lifecycle stage before activation. Current stage: {entity.LifecycleStage}", "-11");
        }

        // ── Four-eyes approval gate ──
        long currentAccountId = long.TryParse(_currentUser.UserId, out var parsedUid) ? parsedUid : 0;
        if (!await _approvalWorkflow.IsApprovedAsync(ApprovalOperationType.StrategyActivation, request.Id, cancellationToken))
        {
            await _approvalWorkflow.RequestApprovalAsync(
                ApprovalOperationType.StrategyActivation,
                request.Id,
                "Strategy",
                $"Activate strategy '{entity.Name}' for {entity.Symbol}/{entity.Timeframe}",
                System.Text.Json.JsonSerializer.Serialize(new { request.Id }),
                currentAccountId,
                cancellationToken);
            return ResponseData<string>.Init(null, false, "Pending four-eyes approval", "-202");
        }

        if (!await _approvalWorkflow.ConsumeApprovalAsync(ApprovalOperationType.StrategyActivation, request.Id, cancellationToken))
            return ResponseData<string>.Init(null, false, "Approval was already consumed by a concurrent request", "-409");

        entity.Status = StrategyStatus.Active;

        // ── Update lifecycle stage on successful activation (skip if already Active to preserve timestamp) ──
        if (entity.LifecycleStage != StrategyLifecycleStage.Active)
        {
            entity.LifecycleStage = StrategyLifecycleStage.Active;
            entity.LifecycleStageEnteredAt = DateTime.UtcNow;
        }

        // ── Auto-queue an initial BacktestRun so the strategy has a performance baseline ──
        var toDate   = DateTime.UtcNow;
        var fromDate = toDate.AddYears(-1);

        var backtestRun = new BacktestRun
        {
            StrategyId     = entity.Id,
            Symbol         = entity.Symbol,
            Timeframe      = entity.Timeframe,
            FromDate       = fromDate,
            ToDate         = toDate,
            InitialBalance = 10_000m,
            Status         = RunStatus.Queued,
            StartedAt      = DateTime.UtcNow
        };

        await _context.GetDbContext().Set<BacktestRun>().AddAsync(backtestRun, cancellationToken);

        // ── Publish StrategyActivatedIntegrationEvent ─────────────────────────
        await _eventBus.SaveAndPublish(_context, new StrategyActivatedIntegrationEvent
        {
            StrategyId  = entity.Id,
            Name        = entity.Name,
            Symbol      = entity.Symbol,
            Timeframe   = entity.Timeframe,
            ActivatedAt = DateTime.UtcNow
        });

        return ResponseData<string>.Init("Activated", true, "Successful", "00");
    }
}
