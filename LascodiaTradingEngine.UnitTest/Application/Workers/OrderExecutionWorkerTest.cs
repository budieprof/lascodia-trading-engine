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
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitOrder;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class OrderExecutionWorkerTest : IDisposable
{
    private readonly Mock<ILogger<OrderExecutionWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IRateLimiter> _mockRateLimiter;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IIntegrationEventService> _mockEventService;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;
    private readonly OrderExecutionWorker _worker;

    public OrderExecutionWorkerTest()
    {
        _mockLogger        = new Mock<ILogger<OrderExecutionWorker>>();
        _mockScopeFactory  = new Mock<IServiceScopeFactory>();
        _mockRateLimiter   = new Mock<IRateLimiter>();
        _mockMediator      = new Mock<IMediator>();
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockEventService  = new Mock<IIntegrationEventService>();
        _meterFactory      = new TestMeterFactory();
        _metrics           = new TradingMetrics(_meterFactory);

        // Wire up IServiceScopeFactory -> IServiceScope -> IServiceProvider
        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_mockWriteContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IIntegrationEventService))).Returns(_mockEventService.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _worker = new OrderExecutionWorker(
            _mockLogger.Object,
            _mockScopeFactory.Object,
            _mockRateLimiter.Object,
            _metrics);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupReadDbContext(List<EngineConfig> configs, List<Order> orders)
    {
        var mockDbContext = new Mock<DbContext>();

        var configDbSet = configs.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);

        var orderDbSet = orders.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Order>()).Returns(orderDbSet.Object);

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
    }

    private void SetupWriteDbContext(List<Order> orders)
    {
        var mockDbContext = new Mock<DbContext>();
        var orderDbSet   = orders.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Order>()).Returns(orderDbSet.Object);

        var qualityLogDbSet = new List<ExecutionQualityLog>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<ExecutionQualityLog>()).Returns(qualityLogDbSet.Object);

        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private void SetupRateLimiterAllowed()
    {
        _mockRateLimiter.Setup(r => r.TryAcquireAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private void SetupRateLimiterDenied()
    {
        _mockRateLimiter.Setup(r => r.TryAcquireAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
    }

    /// <summary>
    /// Invokes the private ProcessPendingOrdersAsync method directly to avoid
    /// the 5-second Task.Delay in ExecuteAsync.
    /// </summary>
    private async Task InvokeProcessPendingOrdersAsync(CancellationToken ct = default)
    {
        var method = typeof(OrderExecutionWorker)
            .GetMethod("ProcessPendingOrdersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_worker, new object[] { ct })!;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessPendingOrders_NoPendingOrders_DoesNotSubmitAnyOrder()
    {
        // Arrange
        SetupReadDbContext(new List<EngineConfig>(), new List<Order>());
        SetupWriteDbContext(new List<Order>());
        SetupRateLimiterAllowed();

        // Act
        await InvokeProcessPendingOrdersAsync();

        // Assert — mediator.Send should never be called for SubmitOrderCommand
        _mockMediator.Verify(
            m => m.Send(It.IsAny<SubmitOrderCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessPendingOrders_WithPendingOrder_SubmitsOrderViaMediator()
    {
        // Arrange
        var pendingOrder = new Order
        {
            Id = 1, Symbol = "EURUSD", Status = OrderStatus.Pending,
            Price = 1.1000m, Quantity = 1, OrderType = OrderType.Buy, StrategyId = 1
        };

        SetupReadDbContext(new List<EngineConfig>(), new List<Order> { pendingOrder });

        // After submit, the order is in Submitted status (not filled)
        var submittedOrder = new Order
        {
            Id = 1, Symbol = "EURUSD", Status = OrderStatus.Submitted,
            Price = 1.1000m, Quantity = 1, OrderType = OrderType.Buy, StrategyId = 1
        };
        SetupWriteDbContext(new List<Order> { submittedOrder });

        SetupRateLimiterAllowed();
        _mockMediator.Setup(m => m.Send(It.IsAny<SubmitOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<SubmitOrderResult>.Init(
                new SubmitOrderResult
                {
                    OrderId = 1, Symbol = "EURUSD", Status = OrderStatus.Submitted,
                    RequestedPrice = 1.1000m, Quantity = 1, OrderType = OrderType.Buy, StrategyId = 1
                }, true, "Successful", "00"));

        // Act
        await InvokeProcessPendingOrdersAsync();

        // Assert
        _mockMediator.Verify(
            m => m.Send(It.Is<SubmitOrderCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessPendingOrders_FilledOrder_PublishesOrderFilledEvent()
    {
        // Arrange
        var pendingOrder = new Order
        {
            Id = 10, Symbol = "GBPUSD", Status = OrderStatus.Pending,
            Price = 1.2500m, Quantity = 0.5m, OrderType = OrderType.Buy,
            StrategyId = 5, Session = TradingSession.London
        };

        SetupReadDbContext(new List<EngineConfig>(), new List<Order> { pendingOrder });
        SetupWriteDbContext(new List<Order>());

        SetupRateLimiterAllowed();
        _mockMediator.Setup(m => m.Send(It.IsAny<SubmitOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<SubmitOrderResult>.Init(
                new SubmitOrderResult
                {
                    OrderId = 10, Symbol = "GBPUSD", Status = OrderStatus.Filled,
                    RequestedPrice = 1.2500m, FilledPrice = 1.2502m, FilledQuantity = 0.5m,
                    Quantity = 0.5m, OrderType = OrderType.Buy, StrategyId = 5,
                    Session = TradingSession.London, FilledAt = DateTime.UtcNow
                }, true, "Successful", "00"));

        // Act
        await InvokeProcessPendingOrdersAsync();

        // Assert — the event bus should publish an OrderFilledIntegrationEvent
        _mockEventService.Verify(
            e => e.SaveAndPublish(It.IsAny<IWriteApplicationDbContext>(), It.Is<OrderFilledIntegrationEvent>(ev => ev.OrderId == 10 && ev.Symbol == "GBPUSD")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessPendingOrders_RateLimitDenied_DefersRemainingOrders()
    {
        // Arrange
        var orders = new List<Order>
        {
            new Order { Id = 1, Symbol = "EURUSD", Status = OrderStatus.Pending, Price = 1.1m, Quantity = 1 },
            new Order { Id = 2, Symbol = "GBPUSD", Status = OrderStatus.Pending, Price = 1.2m, Quantity = 1 }
        };

        SetupReadDbContext(new List<EngineConfig>(), orders);
        SetupWriteDbContext(new List<Order>());
        SetupRateLimiterDenied();

        // Act
        await InvokeProcessPendingOrdersAsync();

        // Assert — no orders should be submitted because rate limiter denied all
        _mockMediator.Verify(
            m => m.Send(It.IsAny<SubmitOrderCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessPendingOrders_ReadsRateLimitConfigFromDatabase()
    {
        // Arrange — provide a rate limit config entry
        var configEntry = new EngineConfig
        {
            Id = 1, Key = "RateLimit:BrokerOrdersPerMinute", Value = "10"
        };
        var pendingOrder = new Order
        {
            Id = 1, Symbol = "EURUSD", Status = OrderStatus.Pending,
            Price = 1.1m, Quantity = 1, OrderType = OrderType.Buy
        };

        SetupReadDbContext(new List<EngineConfig> { configEntry }, new List<Order> { pendingOrder });
        var submittedOrder = new Order
        {
            Id = 1, Symbol = "EURUSD", Status = OrderStatus.Submitted,
            Price = 1.1m, Quantity = 1, OrderType = OrderType.Buy
        };
        SetupWriteDbContext(new List<Order> { submittedOrder });

        SetupRateLimiterAllowed();
        _mockMediator.Setup(m => m.Send(It.IsAny<SubmitOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<SubmitOrderResult>.Init(
                new SubmitOrderResult
                {
                    OrderId = 1, Symbol = "EURUSD", Status = OrderStatus.Submitted,
                    RequestedPrice = 1.1m, Quantity = 1, OrderType = OrderType.Buy
                }, true, "Successful", "00"));

        // Act
        await InvokeProcessPendingOrdersAsync();

        // Assert — rate limiter should be called with the configured max of 10 (from DB)
        _mockRateLimiter.Verify(
            r => r.TryAcquireAsync("broker:orders", 10, TimeSpan.FromMinutes(1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessPendingOrders_SubmissionThrows_IncrementsFailureCountAndContinues()
    {
        // Arrange
        var orders = new List<Order>
        {
            new Order { Id = 1, Symbol = "EURUSD", Status = OrderStatus.Pending, Price = 1.1m, Quantity = 1 },
            new Order { Id = 2, Symbol = "GBPUSD", Status = OrderStatus.Pending, Price = 1.2m, Quantity = 1 }
        };

        SetupReadDbContext(new List<EngineConfig>(), orders);
        SetupWriteDbContext(new List<Order>());
        SetupRateLimiterAllowed();

        // First call throws, second should still be attempted
        _mockMediator.Setup(m => m.Send(It.Is<SubmitOrderCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker error"));
        _mockMediator.Setup(m => m.Send(It.Is<SubmitOrderCommand>(c => c.Id == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<SubmitOrderResult>.Init(
                new SubmitOrderResult
                {
                    OrderId = 2, Symbol = "GBPUSD", Status = OrderStatus.Submitted,
                    RequestedPrice = 1.2m, Quantity = 1, OrderType = OrderType.Buy
                }, true, "Successful", "00"));

        // Act
        await InvokeProcessPendingOrdersAsync();

        // Assert — both orders were attempted
        _mockMediator.Verify(
            m => m.Send(It.Is<SubmitOrderCommand>(c => c.Id == 1), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockMediator.Verify(
            m => m.Send(It.Is<SubmitOrderCommand>(c => c.Id == 2), It.IsAny<CancellationToken>()),
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
