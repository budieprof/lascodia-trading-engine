using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class PositionClosedEventHandlerTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IMediator>                _mockMediator;
    private readonly Mock<ILogger<PositionClosedEventHandler>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>     _mockScopeFactory;
    private readonly Mock<DbContext>                _mockDbContext;

    private readonly PositionClosedEventHandler _handler;

    public PositionClosedEventHandlerTest()
    {
        _mockReadContext  = new Mock<IReadApplicationDbContext>();
        _mockMediator    = new Mock<IMediator>();
        _mockLogger      = new Mock<ILogger<PositionClosedEventHandler>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockDbContext    = new Mock<DbContext>();

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);

        // Wire up IServiceScopeFactory -> IServiceScope -> IServiceProvider
        var mockScope           = new Mock<IServiceScope>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IReadApplicationDbContext)))
            .Returns(_mockReadContext.Object);

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IMediator)))
            .Returns(_mockMediator.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _handler = new PositionClosedEventHandler(_mockScopeFactory.Object, _mockLogger.Object);
    }

    private void SetupDecisionLogs(List<DecisionLog> logs)
    {
        var mockSet = logs.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<DecisionLog>()).Returns(mockSet.Object);
    }

    private static PositionClosedIntegrationEvent CreateEvent(
        long positionId    = 1,
        string symbol      = "EURUSD",
        bool wasProfitable = true,
        decimal realisedPnL = 125.50m,
        decimal entryPrice  = 1.10000m,
        decimal closePrice  = 1.10500m,
        decimal pips        = 50.0m,
        string closeReason  = "TakeProfit",
        PositionDirection direction = PositionDirection.Long)
    {
        return new PositionClosedIntegrationEvent
        {
            PositionId         = positionId,
            Symbol             = symbol,
            Direction          = direction,
            EntryPrice         = entryPrice,
            ClosePrice         = closePrice,
            RealisedPnL        = realisedPnL,
            ActualMagnitudePips = pips,
            WasProfitable      = wasProfitable,
            CloseReason        = closeReason,
            ClosedAt           = DateTime.UtcNow
        };
    }

    // -- Test: Handles event successfully and sends LogDecisionCommand --------

    [Fact]
    public async Task Handle_EventReceived_SendsLogDecisionCommand()
    {
        // Arrange — no existing decision log for this position
        SetupDecisionLogs(new List<DecisionLog>());

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        var @event = CreateEvent(
            positionId: 10,
            symbol: "EURUSD",
            wasProfitable: true,
            realisedPnL: 200.00m,
            entryPrice: 1.10000m,
            closePrice: 1.10500m,
            pips: 50.0m,
            closeReason: "TakeProfit",
            direction: PositionDirection.Long);

        // Act
        await _handler.Handle(@event);

        // Assert — LogDecisionCommand was sent with correct fields
        _mockMediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd =>
                cmd.EntityType   == "Position" &&
                cmd.EntityId     == 10 &&
                cmd.DecisionType == "PositionClosed" &&
                cmd.Outcome      == "Profitable" &&
                cmd.Source       == "PositionClosedEventHandler"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -- Test: Loss outcome sets Outcome = "Loss" -----------------------------

    [Fact]
    public async Task Handle_UnprofitablePosition_SetsOutcomeToLoss()
    {
        // Arrange
        SetupDecisionLogs(new List<DecisionLog>());

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        var @event = CreateEvent(wasProfitable: false);

        // Act
        await _handler.Handle(@event);

        // Assert
        _mockMediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd => cmd.Outcome == "Loss"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -- Test: Idempotency guard — skips if DecisionLog already exists --------

    [Fact]
    public async Task Handle_DecisionLogAlreadyExists_SkipsDuplicate()
    {
        // Arrange — a decision log already exists for this position
        var existingLog = new DecisionLog
        {
            Id           = 99,
            EntityType   = "Position",
            EntityId     = 1,
            DecisionType = "PositionClosed",
            Outcome      = "Profitable",
            Reason       = "TakeProfit: Long EURUSD ...",
            Source       = "PositionClosedEventHandler"
        };

        SetupDecisionLogs(new List<DecisionLog> { existingLog });

        var @event = CreateEvent(positionId: 1);

        // Act
        await _handler.Handle(@event);

        // Assert — LogDecisionCommand should NOT be sent
        _mockMediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -- Test: Idempotency — different position ID is not blocked -------------

    [Fact]
    public async Task Handle_DifferentPositionIdLogged_DoesNotBlockNewPosition()
    {
        // Arrange — decision log exists for position 1 but event is for position 2
        var existingLog = new DecisionLog
        {
            Id           = 99,
            EntityType   = "Position",
            EntityId     = 1,
            DecisionType = "PositionClosed",
            Outcome      = "Profitable",
            Reason       = "...",
            Source       = "PositionClosedEventHandler"
        };

        SetupDecisionLogs(new List<DecisionLog> { existingLog });

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(2L, true, "Successful", "00"));

        var @event = CreateEvent(positionId: 2);

        // Act
        await _handler.Handle(@event);

        // Assert — LogDecisionCommand should be sent for position 2
        _mockMediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd =>
                cmd.EntityId == 2 &&
                cmd.EntityType == "Position" &&
                cmd.DecisionType == "PositionClosed"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -- Test: Gracefully handles missing position (no crash) -----------------

    [Fact]
    public async Task Handle_NoExistingDecisionLog_DoesNotThrow()
    {
        // Arrange — empty decision log table; event refers to a position that may
        // or may not exist. The handler does not look up the Position entity itself;
        // it only checks DecisionLog for idempotency. So this should succeed.
        SetupDecisionLogs(new List<DecisionLog>());

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        var @event = CreateEvent(positionId: 999);

        // Act — should not throw
        var exception = await Record.ExceptionAsync(() => _handler.Handle(@event));

        // Assert
        Assert.Null(exception);

        _mockMediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd => cmd.EntityId == 999),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
