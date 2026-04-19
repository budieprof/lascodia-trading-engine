using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Strategies.Commands.ActivateStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Activates a strategy that has reached the Approved lifecycle stage. Queues an initial
/// backtest run and publishes a <see cref="StrategyActivatedIntegrationEvent"/>.
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
/// Enforces lifecycle stage gate, transitions the strategy to Active status, queues a one-year
/// initial backtest, and publishes a <see cref="StrategyActivatedIntegrationEvent"/>.
/// </summary>
public class ActivateStrategyCommandHandler : IRequestHandler<ActivateStrategyCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;
    private readonly IValidationRunFactory _validationRunFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IPromotionGateValidator _promotionGate;
    private readonly ILogger<ActivateStrategyCommandHandler> _logger;

    public ActivateStrategyCommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService eventBus,
        IValidationRunFactory validationRunFactory,
        TimeProvider timeProvider,
        IPromotionGateValidator promotionGate,
        ILogger<ActivateStrategyCommandHandler> logger)
    {
        _context  = context;
        _eventBus = eventBus;
        _validationRunFactory = validationRunFactory;
        _timeProvider = timeProvider;
        _promotionGate = promotionGate;
        _logger = logger;
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

        // ── Promotion gate stack (items #1/#5/#6 of the pipeline-quality roadmap) ──
        // Deflated Sharpe + PBO-proxy + TCA-adjusted EV + paper-trade duration +
        // backtest-coverage regime proxy + max pairwise correlation. Skip re-checking
        // if the strategy is already Active (idempotent activation path).
        if (entity.Status != StrategyStatus.Active)
        {
            var gate = await _promotionGate.EvaluateAsync(request.Id, cancellationToken);
            if (!gate.Passed)
            {
                _logger.LogWarning(
                    "Activation of strategy {Id} blocked by promotion gates: {Reason}. Diagnostics: {Diagnostics}",
                    request.Id, gate.FailureSummary, string.Join(" | ", gate.Diagnostics));
                return ResponseData<string>.Init(
                    null, false,
                    $"Promotion gates failed: {gate.FailureSummary}. Diagnostics: {string.Join(" | ", gate.Diagnostics)}",
                    "-12");
            }
            _logger.LogInformation(
                "Strategy {Id} cleared promotion gates. {Diagnostics}",
                request.Id, string.Join(" | ", gate.Diagnostics));
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        entity.Status = StrategyStatus.Active;

        // ── Update lifecycle stage on successful activation (skip if already Active to preserve timestamp) ──
        if (entity.LifecycleStage != StrategyLifecycleStage.Active)
        {
            entity.LifecycleStage = StrategyLifecycleStage.Active;
            entity.LifecycleStageEnteredAt = nowUtc;
        }

        // ── Auto-queue an initial BacktestRun so the strategy has a performance baseline ──
        var toDate   = nowUtc;
        var fromDate = toDate.AddYears(-1);

        var backtestRun = await _validationRunFactory.BuildBacktestRunAsync(
            _context.GetDbContext(),
            new BacktestQueueRequest(
                StrategyId: entity.Id,
                Symbol: entity.Symbol,
                Timeframe: entity.Timeframe,
                FromDate: fromDate,
                ToDate: toDate,
                InitialBalance: 10_000m,
                QueueSource: ValidationRunQueueSources.ActivationBaseline,
                ParametersSnapshotJson: entity.ParametersJson,
                ValidationQueueKey: $"backtest:activation:strategy:{entity.Id}"),
            cancellationToken);

        await _context.GetDbContext().Set<BacktestRun>().AddAsync(backtestRun, cancellationToken);

        // ── Publish StrategyActivatedIntegrationEvent ─────────────────────────
        await _eventBus.SaveAndPublish(_context, new StrategyActivatedIntegrationEvent
        {
            StrategyId  = entity.Id,
            Name        = entity.Name,
            Symbol      = entity.Symbol,
            Timeframe   = entity.Timeframe,
            ActivatedAt = nowUtc
        });

        return ResponseData<string>.Init("Activated", true, "Successful", "00");
    }
}
