using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

public class StrategyWorker : BackgroundService, IIntegrationEventHandler<PriceUpdatedIntegrationEvent>
{
    private readonly ILogger<StrategyWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly IEnumerable<IStrategyEvaluator> _evaluators;
    private Timer? _expirySweepTimer;

    public StrategyWorker(
        ILogger<StrategyWorker> logger,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IEnumerable<IStrategyEvaluator> evaluators)
    {
        _logger     = logger;
        _scopeFactory = scopeFactory;
        _eventBus   = eventBus;
        _evaluators = evaluators;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<PriceUpdatedIntegrationEvent, StrategyWorker>();

        _expirySweepTimer = new Timer(RunExpirySweep, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        stoppingToken.Register(() =>
        {
            _expirySweepTimer?.Dispose();
            _eventBus.Unsubscribe<PriceUpdatedIntegrationEvent, StrategyWorker>();
        });

        return Task.CompletedTask;
    }

    public async Task Handle(PriceUpdatedIntegrationEvent @event)
    {
        using var scope = _scopeFactory.CreateScope();
        var context  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var mlScorer = scope.ServiceProvider.GetRequiredService<IMLSignalScorer>();
        var cache    = scope.ServiceProvider.GetRequiredService<ILivePriceCache>();

        var strategies = await context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .Where(x => x.Symbol == @event.Symbol && x.Status == StrategyStatus.Active && !x.IsDeleted)
            .ToListAsync();

        foreach (var strategy in strategies)
        {
            try
            {
                var candles = await context.GetDbContext()
                    .Set<Domain.Entities.Candle>()
                    .Where(x => x.Symbol == strategy.Symbol && x.Timeframe == strategy.Timeframe && x.IsClosed && !x.IsDeleted)
                    .OrderByDescending(x => x.Timestamp)
                    .Take(100)
                    .OrderBy(x => x.Timestamp)
                    .ToListAsync();

                var price = cache.Get(strategy.Symbol);
                if (price is null) continue;

                var evaluator = _evaluators.FirstOrDefault(e => e.StrategyType == strategy.StrategyType);
                if (evaluator is null)
                {
                    _logger.LogWarning("No evaluator found for StrategyType={Type}", strategy.StrategyType);

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "Strategy",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Skipped",
                        Reason       = $"No evaluator registered for StrategyType={strategy.StrategyType}",
                        Source       = "StrategyWorker"
                    });

                    continue;
                }

                var signal = await evaluator.EvaluateAsync(strategy, candles, (price.Value.Bid, price.Value.Ask), CancellationToken.None);
                if (signal is null) continue;

                // ML scoring
                var mlScore = await mlScorer.ScoreAsync(signal, candles, CancellationToken.None);
                signal.MLPredictedDirection = mlScore.PredictedDirection;
                signal.MLPredictedMagnitude = mlScore.PredictedMagnitudePips;
                signal.MLConfidenceScore    = mlScore.ConfidenceScore;
                signal.MLModelId            = mlScore.MLModelId;

                var result = await mediator.Send(new CreateTradeSignalCommand
                {
                    StrategyId             = signal.StrategyId,
                    Symbol                 = signal.Symbol,
                    Direction              = signal.Direction.ToString(),
                    EntryPrice             = signal.EntryPrice,
                    StopLoss               = signal.StopLoss,
                    TakeProfit             = signal.TakeProfit,
                    SuggestedLotSize       = signal.SuggestedLotSize,
                    Confidence             = signal.Confidence,
                    MLPredictedDirection   = signal.MLPredictedDirection?.ToString(),
                    MLPredictedMagnitude   = signal.MLPredictedMagnitude,
                    MLConfidenceScore      = signal.MLConfidenceScore,
                    MLModelId              = signal.MLModelId,
                    MLEnsembleDisagreement = mlScore.EnsembleDisagreement,
                    Timeframe              = strategy.Timeframe,
                    ExpiresAt              = signal.ExpiresAt
                });

                if (result.status && result.data > 0)
                {
                    _eventBus.Publish(new TradeSignalCreatedIntegrationEvent
                    {
                        TradeSignalId = result.data,
                        StrategyId    = signal.StrategyId,
                        Symbol        = signal.Symbol,
                        Direction     = signal.Direction.ToString(),
                        EntryPrice    = signal.EntryPrice
                    });

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = result.data,
                        DecisionType = "SignalGenerated",
                        Outcome      = "Created",
                        Reason       = $"Strategy {strategy.Id} generated {signal.Direction} signal on {signal.Symbol} at {signal.EntryPrice}, Confidence={signal.Confidence:P2}",
                        Source       = "StrategyWorker"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating strategy {StrategyId} for {Symbol}", strategy.Id, @event.Symbol);
            }
        }
    }

    private void RunExpirySweep(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope  = _scopeFactory.CreateScope();
                var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
                var context      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

                var expiredIds = await context.GetDbContext()
                    .Set<Domain.Entities.TradeSignal>()
                    .Where(x => x.Status == TradeSignalStatus.Pending && x.ExpiresAt < DateTime.UtcNow && !x.IsDeleted)
                    .Select(x => x.Id)
                    .ToListAsync();

                foreach (var id in expiredIds)
                    await mediator.Send(new ExpireTradeSignalCommand { Id = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in signal expiry sweep");
            }
        });
    }
}
