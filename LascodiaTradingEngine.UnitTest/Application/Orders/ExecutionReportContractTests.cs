using FluentValidation.TestHelper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.Orders.Commands;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReport;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReportBatch;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Orders;

public sealed class ExecutionReportContractTests
{
    private static readonly string[] EAStatuses =
    [
        "Filled",
        "PartialFill",
        "Rejected",
        "Failed",
        "Cancelled",
        "Dispatched",
        "TransientRetry",
        "SpreadDeferred",
        "Closed",
        "Reversed",
        "Expired",
        "Unmatched",
        "UnmatchedFill",
        "UnmatchedClose",
        "EvictedUnmatched",
        "Duplicate",
        "None",
    ];

    [Theory]
    [MemberData(nameof(Statuses))]
    public void SingleExecutionReportValidator_AcceptsEveryEAStatus(string status)
    {
        var validator = new SubmitExecutionReportCommandValidator();
        var result = validator.TestValidate(new SubmitExecutionReportCommand
        {
            Id = 1,
            Status = status
        });

        result.ShouldNotHaveValidationErrorFor(command => command.Status);
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void BatchExecutionReportValidator_AcceptsEveryEAStatus(string status)
    {
        var validator = new SubmitExecutionReportBatchCommandValidator();
        var result = validator.TestValidate(new SubmitExecutionReportBatchCommand
        {
            Reports =
            [
                new ExecutionReportItem
                {
                    OrderId = 1,
                    Status = status
                }
            ]
        });

        result.ShouldNotHaveValidationErrorFor("Reports[0].Status");
    }

    [Theory]
    [InlineData("Dispatched", OrderStatus.Submitted, true)]
    [InlineData("Filled", OrderStatus.Filled, true)]
    [InlineData("UnmatchedFill", OrderStatus.Filled, true)]
    [InlineData("PartialFill", OrderStatus.PartialFill, true)]
    [InlineData("Rejected", OrderStatus.Rejected, true)]
    [InlineData("Failed", OrderStatus.Rejected, true)]
    [InlineData("Cancelled", OrderStatus.Cancelled, true)]
    [InlineData("Expired", OrderStatus.Expired, true)]
    [InlineData("Closed", OrderStatus.Pending, false)]
    [InlineData("Reversed", OrderStatus.Pending, false)]
    [InlineData("Unmatched", OrderStatus.Pending, false)]
    [InlineData("UnmatchedClose", OrderStatus.Pending, false)]
    public void StatusMapper_MapsEAStatusesToEngineOrderLifecycle(string status, OrderStatus expected, bool expectedMapped)
    {
        var mapped = ExecutionReportStatusMapper.TryMapToOrderStatus(status, out var orderStatus);

        Assert.Equal(expectedMapped, mapped);
        if (expectedMapped)
            Assert.Equal(expected, orderStatus);
    }

    [Fact]
    public async Task Batch_DispatchedThenFilledForSameOrder_PublishesOneFilledEventAndEndsFilled()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await NewContextAsync(connection, CreateOrder());
        var eventBus = new CapturingIntegrationEventService();
        var handler = NewBatchHandler(db, eventBus);

        var response = await handler.Handle(new SubmitExecutionReportBatchCommand
        {
            Reports =
            [
                new ExecutionReportItem
                {
                    OrderId = 1,
                    Status = "Dispatched",
                    BrokerOrderId = "mt5-order-1"
                },
                new ExecutionReportItem
                {
                    OrderId = 1,
                    Status = "Filled",
                    BrokerOrderId = "mt5-order-1",
                    FilledPrice = 1.1050m,
                    FilledQuantity = 1m,
                    FilledAt = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var order = await db.Set<Order>().AsNoTracking().SingleAsync();

        Assert.True(response.status);
        Assert.Equal(2, response.data!.Processed);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(1.1050m, order.FilledPrice);
        Assert.Single(eventBus.PublishedEvents.OfType<OrderFilledIntegrationEvent>());
    }

    [Fact]
    public async Task Batch_FilledThenDispatchedForSameOrder_DoesNotReopenFilledOrder()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await NewContextAsync(connection, CreateOrder());
        var eventBus = new CapturingIntegrationEventService();
        var handler = NewBatchHandler(db, eventBus);

        var response = await handler.Handle(new SubmitExecutionReportBatchCommand
        {
            Reports =
            [
                new ExecutionReportItem
                {
                    OrderId = 1,
                    Status = "Filled",
                    BrokerOrderId = "mt5-order-1",
                    FilledPrice = 1.1050m,
                    FilledQuantity = 1m,
                    FilledAt = DateTime.UtcNow
                },
                new ExecutionReportItem
                {
                    OrderId = 1,
                    Status = "Dispatched",
                    BrokerOrderId = "mt5-order-1"
                }
            ]
        }, CancellationToken.None);

        var order = await db.Set<Order>().AsNoTracking().SingleAsync();

        Assert.True(response.status);
        Assert.Equal(2, response.data!.Processed);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Single(eventBus.PublishedEvents.OfType<OrderFilledIntegrationEvent>());
    }

    [Fact]
    public async Task Batch_DuplicateFilledReports_PublishesFilledEventOnce()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await NewContextAsync(connection, CreateOrder());
        var eventBus = new CapturingIntegrationEventService();
        var handler = NewBatchHandler(db, eventBus);

        var response = await handler.Handle(new SubmitExecutionReportBatchCommand
        {
            Reports =
            [
                new ExecutionReportItem
                {
                    OrderId = 1,
                    Status = "Filled",
                    BrokerOrderId = "mt5-order-1",
                    FilledPrice = 1.1050m,
                    FilledQuantity = 1m,
                    FilledAt = DateTime.UtcNow
                },
                new ExecutionReportItem
                {
                    OrderId = 1,
                    Status = "Filled",
                    BrokerOrderId = "mt5-order-1",
                    FilledPrice = 1.1050m,
                    FilledQuantity = 1m,
                    FilledAt = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        Assert.True(response.status);
        Assert.Equal(2, response.data!.Processed);
        Assert.Single(eventBus.PublishedEvents.OfType<OrderFilledIntegrationEvent>());
    }

    public static IEnumerable<object[]> Statuses()
        => EAStatuses.Select(status => new object[] { status });

    private static SubmitExecutionReportBatchCommandHandler NewBatchHandler(
        TestOrderDbContext db,
        IIntegrationEventService eventBus)
    {
        var ownershipGuard = new Mock<IEAOwnershipGuard>();
        ownershipGuard.Setup(guard => guard.GetCallerAccountId()).Returns(1L);

        return new SubmitExecutionReportBatchCommandHandler(db, eventBus, ownershipGuard.Object);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<TestOrderDbContext> NewContextAsync(SqliteConnection connection, params Order[] orders)
    {
        var options = new DbContextOptionsBuilder<TestOrderDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new TestOrderDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Set<Order>().AddRange(orders);
        await db.SaveChangesAsync();
        return db;
    }

    private static Order CreateOrder()
        => new()
        {
            Id = 1,
            TradingAccountId = 1,
            StrategyId = 10,
            Symbol = "EURUSD",
            Session = TradingSession.London,
            OrderType = OrderType.Buy,
            ExecutionType = ExecutionType.Market,
            Quantity = 1m,
            Price = 1.1000m,
            Status = OrderStatus.Pending,
            IsDeleted = false
        };

    private sealed class TestOrderDbContext(DbContextOptions<TestOrderDbContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbSet<Order> Orders => Set<Order>();

        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(x => x.Id);
            modelBuilder.Entity<Order>().Ignore(x => x.TradeSignal);
            modelBuilder.Entity<Order>().Ignore(x => x.Strategy);
            modelBuilder.Entity<Order>().Ignore(x => x.TradingAccount);
            modelBuilder.Entity<Order>().Ignore(x => x.ExecutionQualityLog);
            modelBuilder.Entity<Order>().Ignore(x => x.PositionScaleOrders);
        }
    }

    private sealed class CapturingIntegrationEventService : IIntegrationEventService
    {
        public List<IntegrationEvent> PublishedEvents { get; } = [];

        public async Task SaveAndPublish(IDbContext context, IntegrationEvent evt)
        {
            await context.SaveChangesAsync();
            PublishedEvents.Add(evt);
        }
    }
}
