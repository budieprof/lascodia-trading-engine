using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Commands.PromotePendingModelStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Promotes a strategy that was parked in <see cref="StrategyLifecycleStage.PendingModel"/>
/// by <c>DeferredCompositeMLRegistrar</c> to its next lifecycle stage, now that an
/// <see cref="MLModel"/> has been trained and activated for its (Symbol, Timeframe) combo.
///
/// This is invoked by <c>PromotePendingModelStrategiesOnActivationHandler</c> when
/// <c>MLModelActivatedIntegrationEvent</c> fires. The command does NOT re-run the full
/// screening gate — it trusts the 14-gate quality check inside <c>MLTrainingWorker</c>
/// that ran before the model was promoted. Re-screening with the new model would
/// duplicate work that <c>MLShadowArbiterWorker</c> and the subsequent backtest
/// (queued automatically on normal activation) already cover.
///
/// The strategy transitions from <c>PendingModel/Paused</c> directly to
/// <c>Draft/Paused</c>, then through the normal lifecycle. Live signal generation
/// does not begin until it reaches <c>Active</c> through standard progression.
/// </summary>
public class PromotePendingModelStrategyCommand : IRequest<ResponseData<long>>
{
    /// <summary>Strategy identifier to promote out of PendingModel.</summary>
    public long StrategyId { get; set; }

    /// <summary>The activated MLModel's database id (for audit trace).</summary>
    public long ActivatedMLModelId { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class PromotePendingModelStrategyCommandValidator
    : AbstractValidator<PromotePendingModelStrategyCommand>
{
    public PromotePendingModelStrategyCommandValidator()
    {
        RuleFor(x => x.StrategyId).GreaterThan(0);
        RuleFor(x => x.ActivatedMLModelId).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Loads the parked strategy, confirms it's in <c>PendingModel</c> state, verifies the
/// activated model's (Symbol, Timeframe) matches, and transitions it to
/// <c>Draft</c> so the normal lifecycle picks it up on the next generation cycle.
/// Safe to invoke multiple times for the same strategy; later calls no-op.
/// </summary>
public class PromotePendingModelStrategyCommandHandler
    : IRequestHandler<PromotePendingModelStrategyCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _writeCtx;
    private readonly IReadApplicationDbContext _readCtx;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PromotePendingModelStrategyCommandHandler> _logger;

    public PromotePendingModelStrategyCommandHandler(
        IWriteApplicationDbContext writeCtx,
        IReadApplicationDbContext readCtx,
        TimeProvider timeProvider,
        ILogger<PromotePendingModelStrategyCommandHandler> logger)
    {
        _writeCtx = writeCtx;
        _readCtx = readCtx;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ResponseData<long>> Handle(
        PromotePendingModelStrategyCommand request,
        CancellationToken ct)
    {
        var strategy = await _writeCtx.GetDbContext()
            .Set<Strategy>()
            .FirstOrDefaultAsync(s => s.Id == request.StrategyId && !s.IsDeleted, ct);

        if (strategy is null)
            return ResponseData<long>.Init(0, false, "Strategy not found", "-14");

        // Idempotent: already-promoted strategies no-op without error.
        if (strategy.LifecycleStage != StrategyLifecycleStage.PendingModel)
        {
            _logger.LogDebug(
                "PromotePendingModelStrategy: strategy {StrategyId} is in stage {Stage}, not PendingModel — skipping",
                strategy.Id, strategy.LifecycleStage);
            return ResponseData<long>.Init(strategy.Id, true, "Already promoted", "00");
        }

        // Safety: verify the activated model actually matches the strategy's combo.
        // In principle the event handler filters by symbol+timeframe, but a defensive
        // check here protects against misrouted events.
        var model = await _readCtx.GetDbContext()
            .Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.ActivatedMLModelId && !m.IsDeleted, ct);

        if (model is null || model.Symbol != strategy.Symbol || model.Timeframe != strategy.Timeframe)
        {
            _logger.LogWarning(
                "PromotePendingModelStrategy: model {ModelId} does not match strategy {StrategyId} combo " +
                "({StrategySym}/{StrategyTf} vs {ModelSym}/{ModelTf}) — refusing to promote",
                request.ActivatedMLModelId, strategy.Id,
                strategy.Symbol, strategy.Timeframe,
                model?.Symbol ?? "<null>", model?.Timeframe);
            return ResponseData<long>.Init(0, false, "Model combo mismatch", "-11");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Transition out of PendingModel. The strategy goes to Draft so the normal
        // lifecycle progression can pick it up. Status stays Paused until something
        // downstream (live backtest, operator, or next generation cycle) moves it
        // forward — we deliberately don't flip to Active here because the model
        // has never been validated on live trades yet.
        strategy.LifecycleStage = StrategyLifecycleStage.Draft;
        strategy.LifecycleStageEnteredAt = now;
        strategy.PauseReason = $"Promoted from PendingModel after MLModel {model.Id} activation; awaiting normal lifecycle progression";

        await _writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "PromotePendingModelStrategy: strategy {StrategyId} ({Symbol}/{Tf}) transitioned PendingModel → Draft on MLModel {ModelId} activation",
            strategy.Id, strategy.Symbol, strategy.Timeframe, model.Id);

        return ResponseData<long>.Init(strategy.Id, true, "Promoted", "00");
    }
}
