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

public class CreateTradeSignalCommand : IRequest<ResponseData<long>>
{
    public long          StrategyId           { get; set; }
    public required string Symbol             { get; set; }
    public required string Direction          { get; set; }   // "Buy" | "Sell"
    public decimal       EntryPrice           { get; set; }
    public decimal?      StopLoss             { get; set; }
    public decimal?      TakeProfit           { get; set; }
    public decimal       SuggestedLotSize     { get; set; }
    public decimal       Confidence           { get; set; }   // 0.0 – 1.0
    public string?       MLPredictedDirection   { get; set; }
    public decimal?      MLPredictedMagnitude   { get; set; }
    public decimal?      MLConfidenceScore      { get; set; }
    public long?         MLModelId              { get; set; }
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
    public DateTime      ExpiresAt              { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

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

        // Create the MLModelPredictionLog record immediately so that
        // MLPredictionOutcomeWorker / drift monitors can resolve the outcome later.
        // Added in the same SaveChangesAsync call — EF Core resolves the temporary
        // TradeSignalId FK automatically, ensuring both writes are atomic.
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
                EnsembleDisagreement   = request.MLEnsembleDisagreement,
                LatencyMs              = request.MLScoringLatencyMs,
                PredictedAt            = DateTime.UtcNow,
            };

            db.Set<MLModelPredictionLog>().Add(predLog);
        }

        //await _context.SaveChangesAsync(cancellationToken);

        await _eventBus.SaveAndPublish(_context,new TradeSignalCreatedIntegrationEvent
        {
            TradeSignalId = entity.Id,
            StrategyId    = entity.StrategyId,
            Symbol        = entity.Symbol,
            Direction     = entity.Direction.ToString(),
            EntryPrice    = entity.EntryPrice,
        });

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
