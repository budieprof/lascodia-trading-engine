using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ApproveTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Bridges the gap between signal generation and order creation.
/// Subscribes to <see cref="TradeSignalCreatedIntegrationEvent"/>, runs the risk checker,
/// and — when the signal passes — approves it and creates a Pending order so that
/// <see cref="OrderExecutionWorker"/> can route it to the broker.
/// </summary>
public class SignalOrderBridgeWorker : BackgroundService, IIntegrationEventHandler<TradeSignalCreatedIntegrationEvent>
{
    private readonly ILogger<SignalOrderBridgeWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;

    public SignalOrderBridgeWorker(
        ILogger<SignalOrderBridgeWorker> logger,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _eventBus     = eventBus;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<TradeSignalCreatedIntegrationEvent, SignalOrderBridgeWorker>();

        stoppingToken.Register(() =>
            _eventBus.Unsubscribe<TradeSignalCreatedIntegrationEvent, SignalOrderBridgeWorker>());

        return Task.CompletedTask;
    }

    public async Task Handle(TradeSignalCreatedIntegrationEvent @event)
    {
        using var scope      = _scopeFactory.CreateScope();
        var readContext      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator         = scope.ServiceProvider.GetRequiredService<IMediator>();
        var riskChecker      = scope.ServiceProvider.GetRequiredService<IRiskChecker>();

        try
        {
            var db = readContext.GetDbContext();

            // Load signal with strategy + risk profile in one query
            var signal = await db.Set<Domain.Entities.TradeSignal>()
                .Include(x => x.Strategy)
                    .ThenInclude(s => s.RiskProfile)
                .FirstOrDefaultAsync(x => x.Id == @event.TradeSignalId && !x.IsDeleted);

            if (signal is null)
            {
                _logger.LogWarning("SignalOrderBridgeWorker: signal {Id} not found", @event.TradeSignalId);
                return;
            }

            if (signal.Status != TradeSignalStatus.Pending)
            {
                _logger.LogInformation(
                    "SignalOrderBridgeWorker: signal {Id} is no longer Pending (status={Status}) — skipping",
                    signal.Id, signal.Status);
                return;
            }

            // Resolve risk profile: strategy-level first, then system default
            var riskProfile = signal.Strategy.RiskProfile
                ?? await db.Set<Domain.Entities.RiskProfile>()
                           .FirstOrDefaultAsync(x => x.IsDefault && !x.IsDeleted);

            if (riskProfile is null)
            {
                _logger.LogError("SignalOrderBridgeWorker: no risk profile for signal {Id} — rejecting", signal.Id);
                await RejectSignalAsync(mediator, signal.Id, "No risk profile configured");
                return;
            }

            // Resolve active trading account
            var account = await db.Set<Domain.Entities.TradingAccount>()
                .FirstOrDefaultAsync(x => x.IsActive && !x.IsDeleted);

            if (account is null)
            {
                _logger.LogError("SignalOrderBridgeWorker: no active trading account — rejecting signal {Id}", signal.Id);
                await RejectSignalAsync(mediator, signal.Id, "No active trading account");
                return;
            }

            // Risk check
            var riskResult = await riskChecker.CheckAsync(signal, riskProfile, CancellationToken.None);

            if (!riskResult.Passed)
            {
                _logger.LogInformation(
                    "SignalOrderBridgeWorker: signal {Id} blocked by risk check — {Reason}",
                    signal.Id, riskResult.BlockReason);

                await RejectSignalAsync(mediator, signal.Id, riskResult.BlockReason!);

                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "TradeSignal",
                    EntityId     = signal.Id,
                    DecisionType = "RiskCheck",
                    Outcome      = "Rejected",
                    Reason       = riskResult.BlockReason!,
                    Source       = "SignalOrderBridgeWorker"
                });

                return;
            }

            // Approve signal
            await mediator.Send(new ApproveTradeSignalCommand { Id = signal.Id });

            // ── Sub-bar execution timing optimizer ─────────────────────────
            // Uses the signal's magnitude prediction and current spread/volatility
            // to decide whether to execute immediately or wait for a better fill.
            var livePriceCache = scope.ServiceProvider.GetService<ILivePriceCache>();
            var executionDelay = EvaluateEntryTiming(signal, livePriceCache, _logger);
            if (executionDelay > TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "SignalOrderBridgeWorker: delaying execution of signal {Id} by {Delay}ms (entry timing optimizer)",
                    signal.Id, executionDelay.TotalMilliseconds);
                await Task.Delay(executionDelay);
            }

            // Create the Pending order
            var orderResult = await mediator.Send(new CreateOrderCommand
            {
                TradeSignalId    = signal.Id,
                StrategyId       = signal.StrategyId,
                TradingAccountId = account.Id,
                Symbol           = signal.Symbol,
                OrderType        = signal.Direction.ToString(),
                ExecutionType    = "Market",
                Quantity         = signal.SuggestedLotSize,
                Price            = 0,   // filled at broker market price
                StopLoss         = signal.StopLoss,
                TakeProfit       = signal.TakeProfit,
                IsPaper          = account.IsPaper
            });

            if (!orderResult.status || orderResult.data <= 0)
            {
                _logger.LogError(
                    "SignalOrderBridgeWorker: CreateOrderCommand failed for signal {Id}", signal.Id);
                return;
            }

            // Write OrderId back onto the signal
            var wdb          = writeContext.GetDbContext();
            var signalEntity = await wdb.Set<Domain.Entities.TradeSignal>()
                .FirstOrDefaultAsync(x => x.Id == signal.Id && !x.IsDeleted);

            if (signalEntity is not null)
            {
                signalEntity.OrderId = orderResult.data;
                await writeContext.SaveChangesAsync(CancellationToken.None);
            }

            _logger.LogInformation(
                "SignalOrderBridgeWorker: order {OrderId} created from signal {SignalId} ({Direction} {Symbol} lot={Lot})",
                orderResult.data, signal.Id, signal.Direction, signal.Symbol, signal.SuggestedLotSize);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Order",
                EntityId     = orderResult.data,
                DecisionType = "OrderCreated",
                Outcome      = "Pending",
                Reason       = $"Signal {signal.Id} approved — {signal.Direction} {signal.Symbol} at {signal.EntryPrice}, lot={signal.SuggestedLotSize}, confidence={signal.Confidence:P0}",
                Source       = "SignalOrderBridgeWorker"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SignalOrderBridgeWorker: unhandled error processing signal {Id}", @event.TradeSignalId);
        }
    }

    private static Task RejectSignalAsync(IMediator mediator, long signalId, string reason)
        => mediator.Send(new RejectTradeSignalCommand { Id = signalId, Reason = reason });

    // ── Sub-bar execution timing ────────────────────────────────────────────

    /// <summary>
    /// Evaluates whether to execute immediately or delay for a better fill within the
    /// current bar. Returns a delay duration (zero = execute now).
    /// <para>
    /// Decision logic:
    /// <list type="bullet">
    ///   <item><b>High confidence + wide spread:</b> wait up to 2 seconds for spread
    ///         to narrow (common during news releases).</item>
    ///   <item><b>Low magnitude prediction:</b> wait up to 1 second — small expected move
    ///         means slippage is a larger fraction of the edge.</item>
    ///   <item><b>High magnitude + tight spread:</b> execute immediately — the edge is
    ///         large enough that timing doesn't matter.</item>
    /// </list>
    /// Maximum delay is capped at 3 seconds to avoid missing the signal window entirely.
    /// </para>
    /// </summary>
    private static TimeSpan EvaluateEntryTiming(
        Domain.Entities.TradeSignal signal,
        ILivePriceCache?            livePriceCache,
        ILogger                     logger)
    {
        const double MaxDelayMs           = 3000;
        const double SpreadThresholdPips  = 2.0;   // above this = "wide" spread
        const double MagnitudeThresholdPips = 5.0;  // below this = "small" expected move

        if (livePriceCache is null)
            return TimeSpan.Zero; // no price data — execute immediately

        decimal? bid = null, ask = null;
        try
        {
            var livePrice = livePriceCache.Get(signal.Symbol);
            if (livePrice is not null)
            {
                bid = livePrice.Value.Bid;
                ask = livePrice.Value.Ask;
            }
        }
        catch
        {
            return TimeSpan.Zero; // cache error — execute immediately
        }

        if (bid is null || ask is null || bid == 0)
            return TimeSpan.Zero;

        // Compute spread in pips (assuming 4/5 digit quotes)
        double spread = (double)(ask.Value - bid.Value);
        bool is5Digit = signal.Symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase);
        double pipMultiplier = is5Digit ? 100.0 : 10000.0;
        double spreadPips = spread * pipMultiplier;

        double magnitudePips = (double)(signal.MLPredictedMagnitude ?? 0);
        double confidence    = (double)signal.Confidence;

        // Decision matrix
        double delayMs = 0;

        // Wide spread — wait for it to narrow
        if (spreadPips > SpreadThresholdPips)
        {
            delayMs += Math.Min(2000, spreadPips / SpreadThresholdPips * 500);
            logger.LogDebug(
                "Entry timing: spread={Spread:F1} pips > threshold — adding {Delay}ms delay",
                spreadPips, delayMs);
        }

        // Small expected move — slippage eats into edge
        if (magnitudePips > 0 && magnitudePips < MagnitudeThresholdPips && confidence < 0.8)
        {
            double magDelay = (1.0 - magnitudePips / MagnitudeThresholdPips) * 1000;
            delayMs += magDelay;
            logger.LogDebug(
                "Entry timing: magnitude={Mag:F1} pips < threshold — adding {Delay}ms delay",
                magnitudePips, magDelay);
        }

        // Cap total delay
        delayMs = Math.Min(delayMs, MaxDelayMs);

        return delayMs > 50 ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero;
    }
}
