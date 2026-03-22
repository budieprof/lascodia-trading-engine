using System.Diagnostics.Metrics;
using System.Reflection;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Commands.ClosePosition;
using LascodiaTradingEngine.Application.Positions.Commands.UpdatePositionPrice;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class PositionWorkerTest : IDisposable
{
    private readonly Mock<ILogger<PositionWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<ILivePriceCache> _mockPriceCache;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IIntegrationEventService> _mockEventService;
    private readonly Mock<IDistributedLock> _mockDistributedLock;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;
    private readonly PositionWorker _worker;

    public PositionWorkerTest()
    {
        _mockLogger          = new Mock<ILogger<PositionWorker>>();
        _mockScopeFactory    = new Mock<IServiceScopeFactory>();
        _mockPriceCache      = new Mock<ILivePriceCache>();
        _mockMediator        = new Mock<IMediator>();
        _mockReadContext     = new Mock<IReadApplicationDbContext>();
        _mockWriteContext    = new Mock<IWriteApplicationDbContext>();
        _mockEventService    = new Mock<IIntegrationEventService>();
        _mockDistributedLock = new Mock<IDistributedLock>();
        _meterFactory        = new TestMeterFactory();
        _metrics             = new TradingMetrics(_meterFactory);

        // Wire up IServiceScopeFactory -> IServiceScope -> IServiceProvider
        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_mockWriteContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IIntegrationEventService))).Returns(_mockEventService.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        // Default: distributed lock always acquired
        var mockHandle = new Mock<IAsyncDisposable>();
        mockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockDistributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockHandle.Object);

        // Default: mediator returns success for all command types used by PositionWorker
        _mockMediator
            .Setup(m => m.Send(It.IsAny<UpdatePositionPriceCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Updated", true, "Successful", "00"));

        _mockMediator
            .Setup(m => m.Send(It.IsAny<ClosePositionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Closed", true, "Successful", "00"));

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        _worker = new PositionWorker(
            _mockLogger.Object,
            _mockScopeFactory.Object,
            _mockPriceCache.Object,
            _metrics,
            _mockDistributedLock.Object);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupOpenPositions(List<Position> positions)
    {
        var mockDbContext = new Mock<DbContext>();
        var positionDbSet = positions.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Position>()).Returns(positionDbSet.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
    }

    private void SetupLivePrice(string symbol, decimal bid, decimal ask)
    {
        _mockPriceCache.Setup(p => p.Get(symbol))
            .Returns((bid, ask, DateTime.UtcNow));
    }

    private void SetupNoPriceAvailable(string symbol)
    {
        _mockPriceCache.Setup(p => p.Get(symbol))
            .Returns(((decimal Bid, decimal Ask, DateTime Timestamp)?)null);
    }

    /// <summary>
    /// Invokes the private UpdatePositionsAsync method directly to avoid
    /// the 10-second Task.Delay in ExecuteAsync.
    /// </summary>
    private async Task InvokeUpdatePositionsAsync(CancellationToken ct = default)
    {
        var method = typeof(PositionWorker)
            .GetMethod("UpdatePositionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_worker, new object[] { ct })!;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePositions_NoOpenPositions_DoesNotSendAnyCommands()
    {
        // Arrange
        SetupOpenPositions(new List<Position>());

        // Act
        await InvokeUpdatePositionsAsync();

        // Assert
        _mockMediator.Verify(
            m => m.Send(It.IsAny<UpdatePositionPriceCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePositions_OpenPosition_UpdatesPositionPriceWithMidPrice()
    {
        // Arrange
        var position = new Position
        {
            Id = 1, Symbol = "EURUSD", Status = PositionStatus.Open,
            Direction = PositionDirection.Long, OpenLots = 1.0m,
            AverageEntryPrice = 1.1000m
        };
        SetupOpenPositions(new List<Position> { position });
        SetupLivePrice("EURUSD", 1.1010m, 1.1012m);

        // Act
        await InvokeUpdatePositionsAsync();

        // Assert — UpdatePositionPriceCommand sent with mid price (1.1010 + 1.1012) / 2 = 1.1011
        _mockMediator.Verify(
            m => m.Send(
                It.Is<UpdatePositionPriceCommand>(c => c.Id == 1 && c.CurrentPrice == 1.1011m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePositions_StopLossHitOnLong_ClosesPositionAndPublishesEvent()
    {
        // Arrange — Long position with SL at 1.0950, current price drops to 1.0940
        var position = new Position
        {
            Id = 5, Symbol = "EURUSD", Status = PositionStatus.Open,
            Direction = PositionDirection.Long, OpenLots = 0.5m,
            AverageEntryPrice = 1.1000m, StopLoss = 1.0950m
        };
        SetupOpenPositions(new List<Position> { position });
        SetupLivePrice("EURUSD", 1.0939m, 1.0941m); // mid = 1.0940

        // Act
        await InvokeUpdatePositionsAsync();

        // Assert — ClosePositionCommand should be sent
        _mockMediator.Verify(
            m => m.Send(
                It.Is<ClosePositionCommand>(c => c.Id == 5 && c.ClosePrice == 1.0940m),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — LogDecisionCommand for StopLossClosure
        _mockMediator.Verify(
            m => m.Send(
                It.Is<LogDecisionCommand>(c => c.EntityId == 5 && c.DecisionType == "StopLossClosure"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — PositionClosedIntegrationEvent published
        _mockEventService.Verify(
            e => e.SaveAndPublish(It.IsAny<IWriteApplicationDbContext>(), It.Is<PositionClosedIntegrationEvent>(
                ev => ev.PositionId == 5 && ev.CloseReason == "StopLoss")),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePositions_TakeProfitHitOnLong_ClosesPositionAndPublishesEvent()
    {
        // Arrange — Long position with TP at 1.2600, current price rises to 1.2610
        var position = new Position
        {
            Id = 7, Symbol = "GBPUSD", Status = PositionStatus.Open,
            Direction = PositionDirection.Long, OpenLots = 1.0m,
            AverageEntryPrice = 1.2500m, TakeProfit = 1.2600m
        };
        SetupOpenPositions(new List<Position> { position });
        SetupLivePrice("GBPUSD", 1.2609m, 1.2611m); // mid = 1.2610

        // Act
        await InvokeUpdatePositionsAsync();

        // Assert — ClosePositionCommand sent
        _mockMediator.Verify(
            m => m.Send(
                It.Is<ClosePositionCommand>(c => c.Id == 7 && c.ClosePrice == 1.2610m),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — LogDecisionCommand for TakeProfitClosure
        _mockMediator.Verify(
            m => m.Send(
                It.Is<LogDecisionCommand>(c => c.EntityId == 7 && c.DecisionType == "TakeProfitClosure"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — PositionClosedIntegrationEvent with TakeProfit reason
        _mockEventService.Verify(
            e => e.SaveAndPublish(It.IsAny<IWriteApplicationDbContext>(), It.Is<PositionClosedIntegrationEvent>(
                ev => ev.PositionId == 7 && ev.CloseReason == "TakeProfit")),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePositions_NoPriceAvailable_SkipsPositionEntirely()
    {
        // Arrange
        var position = new Position
        {
            Id = 3, Symbol = "USDJPY", Status = PositionStatus.Open,
            Direction = PositionDirection.Short, OpenLots = 1.0m,
            AverageEntryPrice = 150.00m, StopLoss = 151.00m, TakeProfit = 149.00m
        };
        SetupOpenPositions(new List<Position> { position });
        SetupNoPriceAvailable("USDJPY");

        // Act
        await InvokeUpdatePositionsAsync();

        // Assert — no price update, no close command
        _mockMediator.Verify(
            m => m.Send(It.IsAny<UpdatePositionPriceCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockMediator.Verify(
            m => m.Send(It.IsAny<ClosePositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePositions_LockNotAcquired_UpdatesPriceButSkipsSlTpCheck()
    {
        // Arrange — lock acquisition returns null (another worker holds it)
        _mockDistributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var position = new Position
        {
            Id = 9, Symbol = "EURUSD", Status = PositionStatus.Open,
            Direction = PositionDirection.Long, OpenLots = 1.0m,
            AverageEntryPrice = 1.1000m, StopLoss = 1.0900m
        };
        SetupOpenPositions(new List<Position> { position });
        SetupLivePrice("EURUSD", 1.0889m, 1.0891m); // mid = 1.0890, below SL

        // Act
        await InvokeUpdatePositionsAsync();

        // Assert — price update should still be sent
        _mockMediator.Verify(
            m => m.Send(It.IsAny<UpdatePositionPriceCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — but close should NOT be sent because lock was not acquired
        _mockMediator.Verify(
            m => m.Send(It.IsAny<ClosePositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePositions_ShortPosition_StopLossHitWhenPriceAboveSl()
    {
        // Arrange — Short position with SL at 151.00, price goes to 151.50
        var position = new Position
        {
            Id = 11, Symbol = "USDJPY", Status = PositionStatus.Open,
            Direction = PositionDirection.Short, OpenLots = 1.0m,
            AverageEntryPrice = 150.00m, StopLoss = 151.00m
        };
        SetupOpenPositions(new List<Position> { position });
        SetupLivePrice("USDJPY", 151.49m, 151.51m); // mid = 151.50 >= SL 151.00

        // Act
        await InvokeUpdatePositionsAsync();

        // Assert — ClosePositionCommand sent for short SL hit
        _mockMediator.Verify(
            m => m.Send(
                It.Is<ClosePositionCommand>(c => c.Id == 11 && c.ClosePrice == 151.50m),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — event published with StopLoss reason
        _mockEventService.Verify(
            e => e.SaveAndPublish(It.IsAny<IWriteApplicationDbContext>(), It.Is<PositionClosedIntegrationEvent>(
                ev => ev.PositionId == 11 && ev.CloseReason == "StopLoss")),
            Times.Once);
    }

    // ── Test helper ──────────────────────────────────────────────────────────

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
