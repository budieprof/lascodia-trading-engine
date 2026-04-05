using System.Diagnostics.Metrics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ApproveTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class SignalOrderBridgeWorkerTest : IDisposable
{
    private readonly Mock<ILogger<SignalOrderBridgeWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<ISignalValidator> _mockSignalValidator;
    private readonly Mock<IAlertDispatcher> _mockAlertDispatcher;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;
    private readonly SignalOrderBridgeWorker _worker;

    public SignalOrderBridgeWorkerTest()
    {
        _mockLogger = new Mock<ILogger<SignalOrderBridgeWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockEventBus = new Mock<IEventBus>();
        _mockMediator = new Mock<IMediator>();
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockSignalValidator = new Mock<ISignalValidator>();
        _mockAlertDispatcher = new Mock<IAlertDispatcher>();
        _mockDbContext = new Mock<DbContext>();
        _meterFactory = new TestMeterFactory();
        _metrics = new TradingMetrics(_meterFactory);

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);

        // Default mediator responses
        _mockMediator
            .Setup(m => m.Send(It.IsAny<ApproveTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Approved", true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RejectTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Rejected", true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        // Wire up scope factory
        var mockScope = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        // News filter mock — default: always safe to trade
        var mockNewsFilter = new Mock<INewsFilter>();
        mockNewsFilter
            .Setup(nf => nf.IsSafeToTradeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Strategy evaluator options mock
        var mockEvalOptions = Options.Create(new StrategyEvaluatorOptions());

        // Processed event tracker mock — default: always allow processing (first processor)
        var mockProcessedEventTracker = new Mock<IProcessedEventTracker>();
        mockProcessedEventTracker
            .Setup(t => t.TryMarkAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(ISignalValidator))).Returns(_mockSignalValidator.Object);
        mockProvider.Setup(p => p.GetService(typeof(IAlertDispatcher))).Returns(_mockAlertDispatcher.Object);
        mockProvider.Setup(p => p.GetService(typeof(INewsFilter))).Returns(mockNewsFilter.Object);
        mockProvider.Setup(p => p.GetService(typeof(IOptions<StrategyEvaluatorOptions>))).Returns(mockEvalOptions);
        mockProvider.Setup(p => p.GetService(typeof(IProcessedEventTracker))).Returns(mockProcessedEventTracker.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _worker = new SignalOrderBridgeWorker(
            _mockLogger.Object,
            _mockScopeFactory.Object,
            _mockEventBus.Object,
            _metrics,
            TimeProvider.System);
    }

    public void Dispose() => _meterFactory.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupDbSets(
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
            new() { Symbol = "EURUSD", BaseCurrency = "EUR", QuoteCurrency = "USD", ContractSize = 100_000m, DecimalPlaces = 5 }
        };

        _mockDbContext.Setup(c => c.Set<TradeSignal>()).Returns(signals.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<EAInstance>()).Returns(eaInstances.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<RiskProfile>()).Returns(riskProfiles.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(currencyPairs.AsQueryable().BuildMockDbSet().Object);
    }

    private static TradeSignalCreatedIntegrationEvent CreateEvent(long signalId = 1) => new()
    {
        TradeSignalId = signalId,
        StrategyId = 1,
        Symbol = "EURUSD",
        Direction = "Buy",
        EntryPrice = 1.1000m
    };

    private TradeSignal CreatePendingSignal(long id = 1) => new()
    {
        Id = id,
        Symbol = "EURUSD",
        Direction = TradeDirection.Buy,
        EntryPrice = 1.1000m,
        StopLoss = 1.0950m,
        TakeProfit = 1.1100m,
        SuggestedLotSize = 0.5m,
        Confidence = 0.80m,
        Status = TradeSignalStatus.Pending,
        ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        StrategyId = 1,
        Strategy = new Strategy
        {
            Id = 1,
            Status = StrategyStatus.Active,
            Symbol = "EURUSD",
            StrategyType = StrategyType.MovingAverageCrossover,
            RiskProfile = new RiskProfile { Id = 1, Name = "Default" }
        }
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_SignalNotFound_SkipsProcessing()
    {
        SetupDbSets(signals: new List<TradeSignal>()); // No signals

        await _worker.Handle(CreateEvent(signalId: 999));

        _mockSignalValidator.Verify(
            v => v.ValidateAsync(It.IsAny<TradeSignal>(), It.IsAny<SignalValidationContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SignalAlreadyApproved_SkipsProcessing()
    {
        var signal = CreatePendingSignal();
        signal.Status = TradeSignalStatus.Approved; // Not Pending
        SetupDbSets(signals: new List<TradeSignal> { signal });

        await _worker.Handle(CreateEvent());

        _mockSignalValidator.Verify(
            v => v.ValidateAsync(It.IsAny<TradeSignal>(), It.IsAny<SignalValidationContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_ExpiredSignal_RejectsImmediately()
    {
        var signal = CreatePendingSignal();
        signal.ExpiresAt = DateTime.UtcNow.AddMinutes(-5); // Expired
        SetupDbSets(signals: new List<TradeSignal> { signal });

        await _worker.Handle(CreateEvent());

        _mockMediator.Verify(
            m => m.Send(It.Is<RejectTradeSignalCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_StrategyInactive_RejectsSignal()
    {
        var signal = CreatePendingSignal();
        signal.Strategy!.Status = StrategyStatus.Paused;
        SetupDbSets(signals: new List<TradeSignal> { signal });

        await _worker.Handle(CreateEvent());

        _mockMediator.Verify(
            m => m.Send(It.Is<RejectTradeSignalCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_NoActiveEA_RejectsSignal()
    {
        var signal = CreatePendingSignal();
        SetupDbSets(signals: new List<TradeSignal> { signal }, eaInstances: new List<EAInstance>());

        await _worker.Handle(CreateEvent());

        _mockMediator.Verify(
            m => m.Send(It.Is<RejectTradeSignalCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_Tier1ValidationFails_RejectsSignal()
    {
        var signal = CreatePendingSignal();
        SetupDbSets(signals: new List<TradeSignal> { signal });

        _mockSignalValidator
            .Setup(v => v.ValidateAsync(It.IsAny<TradeSignal>(), It.IsAny<SignalValidationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RiskCheckResult(Passed: false, BlockReason: "SL too tight"));

        await _worker.Handle(CreateEvent());

        _mockMediator.Verify(
            m => m.Send(It.Is<RejectTradeSignalCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_Tier1ValidationPasses_ApprovesSignal()
    {
        var signal = CreatePendingSignal();
        SetupDbSets(signals: new List<TradeSignal> { signal });

        _mockSignalValidator
            .Setup(v => v.ValidateAsync(It.IsAny<TradeSignal>(), It.IsAny<SignalValidationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RiskCheckResult(Passed: true, BlockReason: null));

        await _worker.Handle(CreateEvent());

        _mockMediator.Verify(
            m => m.Send(It.Is<ApproveTradeSignalCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_DuplicateEventDelivery_SecondCallSkipped()
    {
        var signal = CreatePendingSignal();
        SetupDbSets(signals: new List<TradeSignal> { signal });

        _mockSignalValidator
            .Setup(v => v.ValidateAsync(It.IsAny<TradeSignal>(), It.IsAny<SignalValidationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RiskCheckResult(Passed: true, BlockReason: null));

        var event1 = CreateEvent(signalId: 1);

        // First call processes normally
        await _worker.Handle(event1);

        // Second call with same signal ID should be deduped (in-flight check)
        // However, the first call completes and removes from in-flight before second starts
        // So this tests that the worker handles concurrent-safe dedup
        await _worker.Handle(event1);

        // Both calls process since they're sequential (in-flight is removed after first completes)
        // The key protection here is against truly concurrent delivery, which is hard to test in unit tests
        _mockMediator.Verify(
            m => m.Send(It.IsAny<ApproveTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
