using System.Diagnostics.Metrics;
using System.Reflection;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Commands.ClosePosition;
using LascodiaTradingEngine.Application.Positions.Commands.OpenPosition;
using LascodiaTradingEngine.Application.Positions.Commands.UpdatePositionPrice;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ApproveTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Integration;

/// <summary>
/// Integration-style unit tests that verify the full signal-to-position pipeline by wiring
/// up multiple handlers with shared mock state. Each test drives the pipeline:
///   1. SignalOrderBridgeWorker.Handle  (Tier 1 validation)
///   2. OrderFilledEventHandler.Handle  (creates Position from filled Order)
///   3. PositionWorker.UpdatePositionsAsync  (SL/TP checks, closes positions)
/// </summary>
public class SignalToPositionPipelineTest : IDisposable
{
    // ── Shared mocks ──────────────────────────────────────────────────────────

    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<ILivePriceCache> _mockPriceCache;
    private readonly Mock<ISignalValidator> _mockSignalValidator;
    private readonly Mock<IAlertDispatcher> _mockAlertDispatcher;
    private readonly Mock<IIntegrationEventService> _mockEventService;
    private readonly Mock<IDistributedLock> _mockDistributedLock;
    private readonly Mock<IProcessedEventTracker> _mockProcessedEventTracker;
    private readonly TestMeterFactory _meterFactory;
    private readonly TradingMetrics _metrics;

    // ── Per-handler mocks ─────────────────────────────────────────────────────

    // SignalOrderBridgeWorker
    private readonly Mock<IReadApplicationDbContext> _bridgeReadContext;
    private readonly Mock<DbContext> _bridgeDbContext;
    private readonly Mock<IServiceScopeFactory> _bridgeScopeFactory;
    private readonly Mock<ILogger<SignalOrderBridgeWorker>> _bridgeLogger;
    private readonly SignalOrderBridgeWorker _bridgeWorker;

    // OrderFilledEventHandler
    private readonly Mock<IReadApplicationDbContext> _filledReadContext;
    private readonly Mock<IWriteApplicationDbContext> _filledWriteContext;
    private readonly Mock<DbContext> _filledDbContext;
    private readonly Mock<IServiceScopeFactory> _filledScopeFactory;
    private readonly Mock<ILogger<OrderFilledEventHandler>> _filledLogger;
    private readonly OrderFilledEventHandler _filledHandler;

    // PositionWorker
    private readonly Mock<IReadApplicationDbContext> _posReadContext;
    private readonly Mock<IWriteApplicationDbContext> _posWriteContext;
    private readonly Mock<DbContext> _posDbContext;
    private readonly Mock<IServiceScopeFactory> _posScopeFactory;
    private readonly Mock<ILogger<PositionWorker>> _posLogger;
    private readonly PositionWorker _posWorker;

    public SignalToPositionPipelineTest()
    {
        _mockMediator        = new Mock<IMediator>();
        _mockEventBus        = new Mock<IEventBus>();
        _mockPriceCache      = new Mock<ILivePriceCache>();
        _mockSignalValidator = new Mock<ISignalValidator>();
        _mockAlertDispatcher = new Mock<IAlertDispatcher>();
        _mockEventService    = new Mock<IIntegrationEventService>();
        _mockDistributedLock = new Mock<IDistributedLock>();
        _mockProcessedEventTracker = new Mock<IProcessedEventTracker>();
        _meterFactory        = new TestMeterFactory();
        _metrics             = new TradingMetrics(_meterFactory);

        // Default mediator responses for all command types used across the pipeline
        _mockMediator
            .Setup(m => m.Send(It.IsAny<ApproveTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Approved", true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RejectTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Rejected", true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(100L, true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<UpdatePositionPriceCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Updated", true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<ClosePositionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Closed", true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        // Default distributed lock: always acquired
        var mockHandle = new Mock<IAsyncDisposable>();
        mockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockDistributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockHandle.Object);
        _mockProcessedEventTracker
            .Setup(t => t.TryMarkAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // ── SignalOrderBridgeWorker setup ──────────────────────────────────────

        _bridgeReadContext = new Mock<IReadApplicationDbContext>();
        _bridgeDbContext   = new Mock<DbContext>();
        _bridgeLogger      = new Mock<ILogger<SignalOrderBridgeWorker>>();
        _bridgeScopeFactory = new Mock<IServiceScopeFactory>();

        _bridgeReadContext.Setup(c => c.GetDbContext()).Returns(_bridgeDbContext.Object);

        WireScopeFactory(_bridgeScopeFactory, provider =>
        {
            provider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_bridgeReadContext.Object);
            provider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
            provider.Setup(p => p.GetService(typeof(ISignalValidator))).Returns(_mockSignalValidator.Object);
            provider.Setup(p => p.GetService(typeof(IAlertDispatcher))).Returns(_mockAlertDispatcher.Object);
            provider.Setup(p => p.GetService(typeof(IProcessedEventTracker))).Returns(_mockProcessedEventTracker.Object);

            var mockNewsFilter = new Mock<INewsFilter>();
            mockNewsFilter
                .Setup(nf => nf.IsSafeToTradeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            provider.Setup(p => p.GetService(typeof(INewsFilter))).Returns(mockNewsFilter.Object);
            provider.Setup(p => p.GetService(typeof(IOptions<LascodiaTradingEngine.Application.Common.Options.StrategyEvaluatorOptions>)))
                .Returns(Options.Create(new LascodiaTradingEngine.Application.Common.Options.StrategyEvaluatorOptions()));
        });

        var mockBridgeAuditor = new Mock<ISignalRejectionAuditor>();
        mockBridgeAuditor
            .Setup(a => a.RecordAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var mockBridgeKillSwitch = new Mock<IKillSwitchService>();
        mockBridgeKillSwitch.Setup(k => k.IsGlobalKilledAsync(It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(false));
        mockBridgeKillSwitch.Setup(k => k.IsStrategyKilledAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(false));
        _bridgeWorker = new SignalOrderBridgeWorker(
            _bridgeLogger.Object,
            _bridgeScopeFactory.Object,
            _mockEventBus.Object,
            _metrics,
            TimeProvider.System,
            mockBridgeAuditor.Object,
            mockBridgeKillSwitch.Object);

        // ── OrderFilledEventHandler setup ─────────────────────────────────────

        _filledReadContext  = new Mock<IReadApplicationDbContext>();
        _filledWriteContext = new Mock<IWriteApplicationDbContext>();
        _filledDbContext    = new Mock<DbContext>();
        _filledLogger       = new Mock<ILogger<OrderFilledEventHandler>>();
        _filledScopeFactory = new Mock<IServiceScopeFactory>();

        _filledReadContext.Setup(c => c.GetDbContext()).Returns(_filledDbContext.Object);
        _filledWriteContext.Setup(c => c.GetDbContext()).Returns(_filledDbContext.Object);

        // Mock Database.BeginTransactionAsync for the explicit transaction
        var mockDatabaseFacade = new Mock<DatabaseFacade>(_filledDbContext.Object);
        var mockTransaction    = new Mock<IDbContextTransaction>();
        mockDatabaseFacade
            .Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockTransaction.Object);
        _filledDbContext.Setup(c => c.Database).Returns(mockDatabaseFacade.Object);

        WireScopeFactory(_filledScopeFactory, provider =>
        {
            provider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_filledReadContext.Object);
            provider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_filledWriteContext.Object);
            provider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        });

        _filledHandler = new OrderFilledEventHandler(_filledScopeFactory.Object, _filledLogger.Object);

        // ── PositionWorker setup ──────────────────────────────────────────────

        _posReadContext  = new Mock<IReadApplicationDbContext>();
        _posWriteContext = new Mock<IWriteApplicationDbContext>();
        _posDbContext    = new Mock<DbContext>();
        _posLogger       = new Mock<ILogger<PositionWorker>>();
        _posScopeFactory = new Mock<IServiceScopeFactory>();

        WireScopeFactory(_posScopeFactory, provider =>
        {
            provider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_posReadContext.Object);
            provider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_posWriteContext.Object);
            provider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
            provider.Setup(p => p.GetService(typeof(IIntegrationEventService))).Returns(_mockEventService.Object);
        });

        _posWorker = new PositionWorker(
            _posLogger.Object,
            _posScopeFactory.Object,
            _mockPriceCache.Object,
            _metrics,
            _mockDistributedLock.Object);
    }

    public void Dispose() => _meterFactory.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WireScopeFactory(Mock<IServiceScopeFactory> factory, Action<Mock<IServiceProvider>> configureProvider)
    {
        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        configureProvider(mockProvider);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        factory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
    }

    private void SetupBridgeDbSets(
        List<TradeSignal>? signals = null,
        List<EAInstance>? eaInstances = null,
        List<RiskProfile>? riskProfiles = null,
        List<CurrencyPair>? currencyPairs = null)
    {
        signals ??= new List<TradeSignal>();
        eaInstances ??= new List<EAInstance>
        {
            new() { InstanceId = "ea1", Symbols = "EURUSD", Status = EAInstanceStatus.Active, LastHeartbeat = DateTime.UtcNow }
        };
        riskProfiles ??= new List<RiskProfile>
        {
            new() { Id = 1, Name = "Default", IsDefault = true }
        };
        currencyPairs ??= new List<CurrencyPair>
        {
            new() { Symbol = "EURUSD", BaseCurrency = "EUR", QuoteCurrency = "USD", ContractSize = 100_000m, DecimalPlaces = 5, IsActive = true, PipSize = 0.0001m }
        };

        _bridgeDbContext.Setup(c => c.Set<TradeSignal>()).Returns(signals.AsQueryable().BuildMockDbSet().Object);
        _bridgeDbContext.Setup(c => c.Set<EAInstance>()).Returns(eaInstances.AsQueryable().BuildMockDbSet().Object);
        _bridgeDbContext.Setup(c => c.Set<RiskProfile>()).Returns(riskProfiles.AsQueryable().BuildMockDbSet().Object);
        _bridgeDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(currencyPairs.AsQueryable().BuildMockDbSet().Object);
    }

    private void SetupFilledDbSets(List<Order> orders, List<Position> positions)
    {
        _filledDbContext.Setup(c => c.Set<Order>()).Returns(orders.AsQueryable().BuildMockDbSet().Object);
        _filledDbContext.Setup(c => c.Set<Position>()).Returns(positions.AsQueryable().BuildMockDbSet().Object);
    }

    private void SetupPositionWorkerDbSets(List<Position> positions, List<CurrencyPair>? currencyPairs = null)
    {
        var posDbContext = new Mock<DbContext>();

        posDbContext.Setup(c => c.Set<Position>()).Returns(positions.AsQueryable().BuildMockDbSet().Object);

        currencyPairs ??= new List<CurrencyPair>
        {
            new() { Symbol = "EURUSD", ContractSize = 100_000m }
        };
        posDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(currencyPairs.AsQueryable().BuildMockDbSet().Object);

        _posReadContext.Setup(c => c.GetDbContext()).Returns(posDbContext.Object);
    }

    private void SetupLivePrice(string symbol, decimal bid, decimal ask)
    {
        _mockPriceCache.Setup(p => p.Get(symbol)).Returns((bid, ask, DateTime.UtcNow));
    }

    private async Task InvokeUpdatePositionsAsync(CancellationToken ct = default)
    {
        var method = typeof(PositionWorker)
            .GetMethod("UpdatePositionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_posWorker, new object[] { ct })!;
    }

    private static TradeSignal CreatePendingBuySignal(
        long id = 1,
        decimal entryPrice = 1.1000m,
        decimal stopLoss = 1.0950m,
        decimal takeProfit = 1.1100m,
        DateTime? expiresAt = null)
    {
        return new TradeSignal
        {
            Id               = id,
            Symbol           = "EURUSD",
            Direction        = TradeDirection.Buy,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = 0.10m,
            Confidence       = 0.80m,
            Status           = TradeSignalStatus.Pending,
            ExpiresAt        = expiresAt ?? DateTime.UtcNow.AddMinutes(30),
            StrategyId       = 1,
            Strategy         = new Strategy
            {
                Id           = 1,
                Status       = StrategyStatus.Active,
                Symbol       = "EURUSD",
                StrategyType = StrategyType.MovingAverageCrossover,
                RiskProfile  = new RiskProfile { Id = 1, Name = "Default" }
            }
        };
    }

    private static TradeSignalCreatedIntegrationEvent CreateSignalEvent(long signalId = 1)
    {
        return new TradeSignalCreatedIntegrationEvent
        {
            TradeSignalId = signalId,
            StrategyId    = 1,
            Symbol        = "EURUSD",
            Direction     = "Buy",
            EntryPrice    = 1.1000m
        };
    }

    private static OrderFilledIntegrationEvent CreateOrderFilledEvent(long orderId = 1, decimal filledPrice = 1.1000m)
    {
        return new OrderFilledIntegrationEvent
        {
            OrderId     = orderId,
            FilledPrice = filledPrice,
            Symbol      = "EURUSD",
            FilledAt    = DateTime.UtcNow
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullPipeline_SignalApproved_OrderFilled_PositionOpened_SlHit_PositionClosed()
    {
        // ── Step 1: SignalOrderBridgeWorker approves the signal ────────────

        var signal = CreatePendingBuySignal(
            id: 1, entryPrice: 1.1000m, stopLoss: 1.0950m, takeProfit: 1.1100m);

        SetupBridgeDbSets(signals: new List<TradeSignal> { signal });

        _mockSignalValidator
            .Setup(v => v.ValidateAsync(
                It.IsAny<TradeSignal>(),
                It.IsAny<SignalValidationContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RiskCheckResult(Passed: true, BlockReason: null));

        await _bridgeWorker.Handle(CreateSignalEvent(signalId: 1));

        // Assert: signal was approved
        _mockMediator.Verify(
            m => m.Send(It.Is<ApproveTradeSignalCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: signal was NOT rejected
        _mockMediator.Verify(
            m => m.Send(It.IsAny<RejectTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // ── Step 2: OrderFilledEventHandler creates a Position ────────────

        var filledOrder = new Order
        {
            Id             = 10,
            Symbol         = "EURUSD",
            OrderType      = OrderType.Buy,
            Quantity       = 0.10m,
            FilledQuantity = 0.10m,
            StopLoss       = 1.0950m,
            TakeProfit     = 1.1100m,
            IsPaper        = false,
            IsDeleted      = false,
            Status         = OrderStatus.Filled
        };

        SetupFilledDbSets(
            orders: new List<Order> { filledOrder },
            positions: new List<Position>());

        await _filledHandler.Handle(CreateOrderFilledEvent(orderId: 10, filledPrice: 1.1000m));

        // Assert: OpenPositionCommand was sent with correct parameters
        _mockMediator.Verify(m => m.Send(
            It.Is<OpenPositionCommand>(cmd =>
                cmd.Symbol            == "EURUSD" &&
                cmd.Direction         == "Long" &&
                cmd.OpenLots          == 0.10m &&
                cmd.AverageEntryPrice == 1.1000m &&
                cmd.StopLoss          == 1.0950m &&
                cmd.TakeProfit        == 1.1100m &&
                cmd.IsPaper           == false &&
                cmd.OpenOrderId       == 10),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: LogDecisionCommand was sent for position opened
        _mockMediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd =>
                cmd.EntityType   == "Order" &&
                cmd.EntityId     == 10 &&
                cmd.DecisionType == "PositionOpened" &&
                cmd.Source       == "OrderFilledEventHandler"),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // ── Step 3: PositionWorker detects SL hit and closes position ─────

        var openPosition = new Position
        {
            Id                = 100,
            Symbol            = "EURUSD",
            Status            = PositionStatus.Open,
            Direction         = PositionDirection.Long,
            OpenLots          = 0.10m,
            AverageEntryPrice = 1.1000m,
            StopLoss          = 1.0950m,
            TakeProfit        = 1.1100m,
            OpenOrderId       = 10
        };

        SetupPositionWorkerDbSets(new List<Position> { openPosition });

        // Price drops below SL: bid=1.0939, ask=1.0941 => mid=1.0940 < SL 1.0950
        SetupLivePrice("EURUSD", 1.0939m, 1.0941m);

        await InvokeUpdatePositionsAsync();

        // Assert: ClosePositionCommand sent with correct close price
        _mockMediator.Verify(
            m => m.Send(
                It.Is<ClosePositionCommand>(c => c.Id == 100 && c.ClosePrice == 1.0940m),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: LogDecisionCommand for StopLossClosure
        _mockMediator.Verify(
            m => m.Send(
                It.Is<LogDecisionCommand>(c => c.EntityId == 100 && c.DecisionType == "StopLossClosure"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: PositionClosedIntegrationEvent published with StopLoss reason
        _mockEventService.Verify(
            e => e.SaveAndPublish(
                It.IsAny<IWriteApplicationDbContext>(),
                It.Is<PositionClosedIntegrationEvent>(ev =>
                    ev.PositionId == 100 && ev.CloseReason == "StopLoss")),
            Times.Once);
    }

    [Fact]
    public async Task FullPipeline_SignalRejected_NeverReachesOrder()
    {
        // ── Step 1: Signal is expired, bridge worker rejects it ───────────

        var signal = CreatePendingBuySignal(
            id: 2,
            expiresAt: DateTime.UtcNow.AddMinutes(-5)); // Already expired

        SetupBridgeDbSets(signals: new List<TradeSignal> { signal });

        await _bridgeWorker.Handle(CreateSignalEvent(signalId: 2));

        // Assert: signal was rejected due to expiry
        _mockMediator.Verify(
            m => m.Send(It.Is<RejectTradeSignalCommand>(c => c.Id == 2), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: signal was NOT approved
        _mockMediator.Verify(
            m => m.Send(It.IsAny<ApproveTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: no OpenPositionCommand was ever sent (order handler never invoked)
        _mockMediator.Verify(
            m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: no position close commands were sent
        _mockMediator.Verify(
            m => m.Send(It.IsAny<ClosePositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FullPipeline_TakeProfitHit_PositionClosedWithProfit()
    {
        // ── Step 1: SignalOrderBridgeWorker approves the signal ────────────

        var signal = CreatePendingBuySignal(
            id: 3, entryPrice: 1.1000m, stopLoss: 1.0950m, takeProfit: 1.1100m);

        SetupBridgeDbSets(signals: new List<TradeSignal> { signal });

        _mockSignalValidator
            .Setup(v => v.ValidateAsync(
                It.IsAny<TradeSignal>(),
                It.IsAny<SignalValidationContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RiskCheckResult(Passed: true, BlockReason: null));

        await _bridgeWorker.Handle(CreateSignalEvent(signalId: 3));

        // Assert: signal was approved
        _mockMediator.Verify(
            m => m.Send(It.Is<ApproveTradeSignalCommand>(c => c.Id == 3), It.IsAny<CancellationToken>()),
            Times.Once);

        // ── Step 2: OrderFilledEventHandler creates a Position ────────────

        var filledOrder = new Order
        {
            Id             = 20,
            Symbol         = "EURUSD",
            OrderType      = OrderType.Buy,
            Quantity       = 0.10m,
            FilledQuantity = 0.10m,
            StopLoss       = 1.0950m,
            TakeProfit     = 1.1100m,
            IsPaper        = false,
            IsDeleted      = false,
            Status         = OrderStatus.Filled
        };

        SetupFilledDbSets(
            orders: new List<Order> { filledOrder },
            positions: new List<Position>());

        await _filledHandler.Handle(CreateOrderFilledEvent(orderId: 20, filledPrice: 1.1000m));

        // Assert: OpenPositionCommand was sent
        _mockMediator.Verify(m => m.Send(
            It.Is<OpenPositionCommand>(cmd =>
                cmd.Symbol      == "EURUSD" &&
                cmd.Direction   == "Long" &&
                cmd.OpenOrderId == 20),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // ── Step 3: PositionWorker detects TP hit and closes position ─────

        var openPosition = new Position
        {
            Id                = 200,
            Symbol            = "EURUSD",
            Status            = PositionStatus.Open,
            Direction         = PositionDirection.Long,
            OpenLots          = 0.10m,
            AverageEntryPrice = 1.1000m,
            StopLoss          = 1.0950m,
            TakeProfit        = 1.1100m,
            OpenOrderId       = 20
        };

        SetupPositionWorkerDbSets(new List<Position> { openPosition });

        // Price rises above TP: bid=1.1109, ask=1.1111 => mid=1.1110 > TP 1.1100
        SetupLivePrice("EURUSD", 1.1109m, 1.1111m);

        await InvokeUpdatePositionsAsync();

        // Assert: ClosePositionCommand sent with correct close price
        _mockMediator.Verify(
            m => m.Send(
                It.Is<ClosePositionCommand>(c => c.Id == 200 && c.ClosePrice == 1.1110m),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: LogDecisionCommand for TakeProfitClosure
        _mockMediator.Verify(
            m => m.Send(
                It.Is<LogDecisionCommand>(c => c.EntityId == 200 && c.DecisionType == "TakeProfitClosure"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: PositionClosedIntegrationEvent published with TakeProfit reason
        _mockEventService.Verify(
            e => e.SaveAndPublish(
                It.IsAny<IWriteApplicationDbContext>(),
                It.Is<PositionClosedIntegrationEvent>(ev =>
                    ev.PositionId == 200 && ev.CloseReason == "TakeProfit")),
            Times.Once);
    }

    // ── Test helper ───────────────────────────────────────────────────────────

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}
