using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Lascodia.Trading.Engine.IntegrationEventLogEF.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;

namespace LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates a new trade signal from a strategy evaluation, optionally enriched with ML model
/// predictions. The signal is persisted in Pending status and a <see cref="TradeSignalCreatedIntegrationEvent"/>
/// is published. When an ML model scored the signal, a <see cref="Domain.Entities.MLModelPredictionLog"/> is also created.
/// </summary>
public class CreateTradeSignalCommand : IRequest<ResponseData<long>>
{
    /// <summary>Strategy that generated this signal.</summary>
    public long          StrategyId           { get; set; }
    /// <summary>Currency pair symbol (e.g. "EURUSD").</summary>
    public required string Symbol             { get; set; }
    /// <summary>Trade direction: "Buy" or "Sell".</summary>
    public required string Direction          { get; set; }   // "Buy" | "Sell"
    /// <summary>Suggested entry price from the strategy evaluator.</summary>
    public decimal       EntryPrice           { get; set; }
    /// <summary>Suggested stop-loss price level.</summary>
    public decimal?      StopLoss             { get; set; }
    /// <summary>Suggested take-profit price level.</summary>
    public decimal?      TakeProfit           { get; set; }
    /// <summary>Recommended position size in lots.</summary>
    public decimal       SuggestedLotSize     { get; set; }
    /// <summary>Strategy confidence score between 0.0 and 1.0.</summary>
    public decimal       Confidence           { get; set; }   // 0.0 – 1.0
    /// <summary>ML model's predicted trade direction, if scored.</summary>
    public string?       MLPredictedDirection   { get; set; }
    /// <summary>ML model's predicted price movement magnitude in pips.</summary>
    public decimal?      MLPredictedMagnitude   { get; set; }
    /// <summary>ML model's confidence score for the prediction.</summary>
    public decimal?      MLConfidenceScore      { get; set; }
    /// <summary>Identifier of the ML model that scored this signal.</summary>
    public long?         MLModelId              { get; set; }
    /// <summary>Raw (uncalibrated) probability from the ML model.</summary>
    public decimal?      MLRawProbability       { get; set; }
    /// <summary>Calibrated probability after Platt scaling or isotonic regression.</summary>
    public decimal?      MLCalibratedProbability { get; set; }
    /// <summary>Final served calibrated probability after any runtime adjustments.</summary>
    public decimal?      MLServedCalibratedProbability { get; set; }
    /// <summary>Decision threshold used to convert probability to a trade/no-trade decision.</summary>
    public decimal?      MLDecisionThresholdUsed { get; set; }
    /// <summary>Conformal calibration record active when this prediction was served.</summary>
    public long?         MLConformalCalibrationId { get; set; }
    /// <summary>Prediction-time conformal threshold used to create the served prediction set.</summary>
    public double?       MLConformalThresholdUsed { get; set; }
    /// <summary>Prediction-time conformal target coverage.</summary>
    public double?       MLConformalTargetCoverageUsed { get; set; }
    /// <summary>JSON array containing labels in the served conformal prediction set.</summary>
    public string?       MLConformalPredictionSetJson { get; set; }
    /// <summary>
    /// Standard deviation of individual ensemble learner probabilities at scoring time.
    /// Stored on <c>MLModelPredictionLog</c> for live disagreement monitoring.
    /// </summary>
    public decimal?      MLEnsembleDisagreement { get; set; }
    /// <summary>
    /// Timeframe of the strategy that generated this signal. Required to correctly
    /// set <c>MLModelPredictionLog.Timeframe</c> when an ML model scored the signal.
    /// </summary>
    public Timeframe     Timeframe              { get; set; } = Timeframe.H1;
    /// <summary>
    /// Wall-clock milliseconds taken by <c>IMLSignalScorer.ScoreAsync</c>.
    /// Measured by the calling strategy worker using a <c>Stopwatch</c> around the
    /// <c>ScoreAsync</c> call and passed through here so it can be stored on
    /// <c>MLModelPredictionLog.LatencyMs</c> for P50/P95/P99 monitoring.
    /// </summary>
    public int?          MLScoringLatencyMs     { get; set; }
    /// <summary>Raw feature vector JSON emitted by ML scoring for diagnostics.</summary>
    public string?       MLRawFeaturesJson      { get; set; }
    public DateTime      ExpiresAt              { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates signal inputs including strategy Id, symbol, direction, entry price, lot size, and confidence range.</summary>
public class CreateTradeSignalCommandValidator : AbstractValidator<CreateTradeSignalCommand>
{
    public CreateTradeSignalCommandValidator()
    {
        RuleFor(x => x.StrategyId)
            .GreaterThan(0).WithMessage("StrategyId must be greater than zero");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Direction)
            .NotEmpty().WithMessage("Direction cannot be empty")
            .Must(d => d == "Buy" || d == "Sell").WithMessage("Direction must be 'Buy' or 'Sell'");

        RuleFor(x => x.EntryPrice)
            .GreaterThan(0).WithMessage("EntryPrice must be greater than zero");

        RuleFor(x => x.SuggestedLotSize)
            .GreaterThan(0).WithMessage("SuggestedLotSize must be greater than zero");

        RuleFor(x => x.Confidence)
            .InclusiveBetween(0m, 1m).WithMessage("Confidence must be between 0 and 1");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Persists the trade signal in Pending status, publishes a <see cref="TradeSignalCreatedIntegrationEvent"/>,
/// and creates an <see cref="Domain.Entities.MLModelPredictionLog"/> when ML scoring metadata is present.
/// </summary>
public class CreateTradeSignalCommandHandler : IRequestHandler<CreateTradeSignalCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public CreateTradeSignalCommandHandler(IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<long>> Handle(CreateTradeSignalCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.TradeSignal
        {
            StrategyId           = request.StrategyId,
            Symbol               = request.Symbol,
            Direction            = Enum.Parse<TradeDirection>(request.Direction, ignoreCase: true),
            EntryPrice           = request.EntryPrice,
            StopLoss             = request.StopLoss,
            TakeProfit           = request.TakeProfit,
            SuggestedLotSize     = request.SuggestedLotSize,
            Confidence           = request.Confidence,
            MLPredictedDirection = request.MLPredictedDirection is not null
                ? Enum.Parse<TradeDirection>(request.MLPredictedDirection, ignoreCase: true)
                : null,
            MLPredictedMagnitude = request.MLPredictedMagnitude,
            MLConfidenceScore    = request.MLConfidenceScore,
            MLModelId            = request.MLModelId,
            Status               = TradeSignalStatus.Pending,
            GeneratedAt          = DateTime.UtcNow,
            ExpiresAt            = request.ExpiresAt
        };

        var db = _context.GetDbContext();
        await db.Set<Domain.Entities.TradeSignal>()
            .AddAsync(entity, cancellationToken);

        // Persist the signal first so it gets a real DB-assigned Id, then write
        // the prediction log with the resolved FK. Writing both in the same
        // SaveChangesAsync was failing on ResilientTransaction retries because the
        // temp TradeSignalId wasn't being resolved correctly across retry boundaries.
        await _eventBus.SaveAndPublish(_context, new TradeSignalCreatedIntegrationEvent
        {
            TradeSignalId = entity.Id,
            StrategyId    = entity.StrategyId,
            Symbol        = entity.Symbol,
            Direction     = entity.Direction.ToString(),
            EntryPrice    = entity.EntryPrice,
        });

        // Now entity.Id is the real DB-assigned value — safe to use as FK.
        if (request.MLModelId.HasValue)
        {
            var predLog = new MLModelPredictionLog
            {
                TradeSignalId          = entity.Id,
                MLModelId              = request.MLModelId.Value,
                ModelRole              = ModelRole.Champion,
                Symbol                 = entity.Symbol,
                Timeframe              = request.Timeframe,
                PredictedDirection     = entity.MLPredictedDirection ?? entity.Direction,
                PredictedMagnitudePips = entity.MLPredictedMagnitude ?? 0m,
                ConfidenceScore        = entity.MLConfidenceScore ?? 0m,
                RawProbability         = request.MLRawProbability,
                CalibratedProbability  = request.MLCalibratedProbability,
                ServedCalibratedProbability = request.MLServedCalibratedProbability,
                DecisionThresholdUsed  = request.MLDecisionThresholdUsed,
                MLConformalCalibrationId = request.MLConformalCalibrationId,
                ConformalThresholdUsed = request.MLConformalThresholdUsed,
                ConformalTargetCoverageUsed = request.MLConformalTargetCoverageUsed,
                ConformalPredictionSetJson = request.MLConformalPredictionSetJson,
                EnsembleDisagreement   = request.MLEnsembleDisagreement,
                LatencyMs              = request.MLScoringLatencyMs,
                RawFeaturesJson        = request.MLRawFeaturesJson,
                PredictedAt            = DateTime.UtcNow,
            };

            db.Set<MLModelPredictionLog>().Add(predLog);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
