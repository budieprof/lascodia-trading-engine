using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Positions.Commands.OpenPosition;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class OrderFilledEventHandlerTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IMediator>                _mockMediator;
    private readonly Mock<ILogger<OrderFilledEventHandler>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>     _mockScopeFactory;
    private readonly Mock<DbContext>                _mockDbContext;

    private readonly OrderFilledEventHandler _handler;

    public OrderFilledEventHandlerTest()
    {
        _mockReadContext  = new Mock<IReadApplicationDbContext>();
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockMediator    = new Mock<IMediator>();
        _mockLogger      = new Mock<ILogger<OrderFilledEventHandler>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockDbContext    = new Mock<DbContext>();

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);

        // Mock Database.BeginTransactionAsync for the explicit transaction in HandleCoreAsync
        var mockDatabaseFacade = new Mock<DatabaseFacade>(_mockDbContext.Object);
        var mockTransaction = new Mock<IDbContextTransaction>();
        mockDatabaseFacade
            .Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockTransaction.Object);
        _mockDbContext.Setup(c => c.Database).Returns(mockDatabaseFacade.Object);

        // Wire up IServiceScopeFactory → IServiceScope → IServiceProvider
        var mockScope           = new Mock<IServiceScope>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IReadApplicationDbContext)))
            .Returns(_mockReadContext.Object);

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IWriteApplicationDbContext)))
            .Returns(_mockWriteContext.Object);

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IMediator)))
            .Returns(_mockMediator.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _handler = new OrderFilledEventHandler(_mockScopeFactory.Object, _mockLogger.Object);
    }

    private void SetupOrders(List<Order> orders)
    {
        var mockOrderSet = orders.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Order>()).Returns(mockOrderSet.Object);
    }

    private void SetupPositions(List<Position> positions)
    {
        var mockPositionSet = positions.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(mockPositionSet.Object);
    }

    private static OrderFilledIntegrationEvent CreateEvent(long orderId = 1, decimal filledPrice = 1.10000m)
    {
        return new OrderFilledIntegrationEvent
        {
            OrderId     = orderId,
            FilledPrice = filledPrice,
            Symbol      = "EURUSD",
            FilledAt    = DateTime.UtcNow
        };
    }

    // ── Test: Order not found ────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OrderNotFound_LogsWarningAndReturns()
    {
        // Arrange
        SetupOrders(new List<Order>());
        SetupPositions(new List<Position>());

        var @event = CreateEvent(orderId: 999);

        // Act
        await _handler.Handle(@event);

        // Assert — no position command should be sent
        _mockMediator.Verify(
            m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test: Idempotency — position already exists ──────────────────────────

    [Fact]
    public async Task Handle_PositionAlreadyExistsForOrder_SkipsCreation()
    {
        // Arrange
        var order = new Order
        {
            Id        = 1,
            Symbol    = "EURUSD",
            OrderType = OrderType.Buy,
            Quantity  = 0.10m,
            IsDeleted = false
        };

        var existingPosition = new Position
        {
            Id          = 100,
            OpenOrderId = 1,
            IsDeleted   = false
        };

        SetupOrders(new List<Order> { order });
        SetupPositions(new List<Position> { existingPosition });

        var @event = CreateEvent(orderId: 1);

        // Act
        await _handler.Handle(@event);

        // Assert — no position command should be sent
        _mockMediator.Verify(
            m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test: Successful Buy order → Long position ───────────────────────────

    [Fact]
    public async Task Handle_BuyOrderFilled_CreatesLongPosition()
    {
        // Arrange
        var order = new Order
        {
            Id             = 1,
            Symbol         = "EURUSD",
            OrderType      = OrderType.Buy,
            Quantity       = 0.10m,
            FilledQuantity = 0.10m,
            StopLoss       = 1.09000m,
            TakeProfit     = 1.12000m,
            IsPaper        = false,
            IsDeleted      = false
        };

        SetupOrders(new List<Order> { order });
        SetupPositions(new List<Position>());

        var successResponse = ResponseData<long>.Init(42L, true, "Successful", "00");

        _mockMediator
            .Setup(m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResponse);

        // Also setup LogDecisionCommand
        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        var @event = CreateEvent(orderId: 1, filledPrice: 1.10500m);

        // Act
        await _handler.Handle(@event);

        // Assert — OpenPositionCommand with Direction = "Long"
        _mockMediator.Verify(m => m.Send(
            It.Is<OpenPositionCommand>(cmd =>
                cmd.Symbol            == "EURUSD" &&
                cmd.Direction         == "Long" &&
                cmd.OpenLots          == 0.10m &&
                cmd.AverageEntryPrice == 1.10500m &&
                cmd.StopLoss          == 1.09000m &&
                cmd.TakeProfit        == 1.12000m &&
                cmd.IsPaper           == false &&
                cmd.OpenOrderId       == 1),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — LogDecisionCommand was also sent
        _mockMediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd =>
                cmd.EntityType   == "Order" &&
                cmd.EntityId     == 1 &&
                cmd.DecisionType == "PositionOpened" &&
                cmd.Source       == "OrderFilledEventHandler"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test: Successful Sell order → Short position ─────────────────────────

    [Fact]
    public async Task Handle_SellOrderFilled_CreatesShortPosition()
    {
        // Arrange
        var order = new Order
        {
            Id             = 2,
            Symbol         = "GBPUSD",
            OrderType      = OrderType.Sell,
            Quantity       = 0.50m,
            FilledQuantity = null, // fallback to Quantity
            StopLoss       = 1.28000m,
            TakeProfit     = 1.25000m,
            IsPaper        = true,
            IsDeleted      = false
        };

        SetupOrders(new List<Order> { order });
        SetupPositions(new List<Position>());

        var successResponse = ResponseData<long>.Init(99L, true, "Successful", "00");

        _mockMediator
            .Setup(m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResponse);

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(2L, true, "Successful", "00"));

        var @event = CreateEvent(orderId: 2, filledPrice: 1.26500m);

        // Act
        await _handler.Handle(@event);

        // Assert — OpenPositionCommand with Direction = "Short"
        _mockMediator.Verify(m => m.Send(
            It.Is<OpenPositionCommand>(cmd =>
                cmd.Symbol    == "GBPUSD" &&
                cmd.Direction == "Short" &&
                cmd.OpenLots  == 0.50m &&
                cmd.IsPaper   == true &&
                cmd.OpenOrderId == 2),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test: OpenPositionCommand fails → logs error, no decision log ────────

    [Fact]
    public async Task Handle_OpenPositionCommandFails_LogsErrorAndReturns()
    {
        // Arrange
        var order = new Order
        {
            Id        = 3,
            Symbol    = "USDJPY",
            OrderType = OrderType.Buy,
            Quantity  = 1.00m,
            IsDeleted = false
        };

        SetupOrders(new List<Order> { order });
        SetupPositions(new List<Position>());

        var failureResponse = ResponseData<long>.Init(0L, false, "Validation failed", "-11");

        _mockMediator
            .Setup(m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResponse);

        var @event = CreateEvent(orderId: 3, filledPrice: 150.500m);

        // Act
        await _handler.Handle(@event);

        // Assert — OpenPositionCommand was sent
        _mockMediator.Verify(
            m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — LogDecisionCommand should NOT be sent because the open failed
        _mockMediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test: Deleted order is not found ─────────────────────────────────────

    [Fact]
    public async Task Handle_DeletedOrder_TreatedAsNotFound()
    {
        // Arrange — order exists but is soft-deleted
        var order = new Order
        {
            Id        = 5,
            Symbol    = "EURUSD",
            OrderType = OrderType.Buy,
            Quantity  = 0.10m,
            IsDeleted = true
        };

        SetupOrders(new List<Order> { order });
        SetupPositions(new List<Position>());

        var @event = CreateEvent(orderId: 5);

        // Act
        await _handler.Handle(@event);

        // Assert — no position command should be sent (order filtered by IsDeleted check)
        _mockMediator.Verify(
            m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test: FilledQuantity fallback to Quantity when null ───────────────────

    [Fact]
    public async Task Handle_FilledQuantityNull_FallsBackToQuantity()
    {
        // Arrange
        var order = new Order
        {
            Id             = 7,
            Symbol         = "AUDUSD",
            OrderType      = OrderType.Buy,
            Quantity       = 0.25m,
            FilledQuantity = null,
            IsDeleted      = false
        };

        SetupOrders(new List<Order> { order });
        SetupPositions(new List<Position>());

        var successResponse = ResponseData<long>.Init(77L, true, "Successful", "00");

        _mockMediator
            .Setup(m => m.Send(It.IsAny<OpenPositionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResponse);

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        var @event = CreateEvent(orderId: 7, filledPrice: 0.67500m);

        // Act
        await _handler.Handle(@event);

        // Assert — lots should equal Quantity (0.25) since FilledQuantity is null
        _mockMediator.Verify(m => m.Send(
            It.Is<OpenPositionCommand>(cmd => cmd.OpenLots == 0.25m),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
