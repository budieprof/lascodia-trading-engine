using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.Orders.Commands.CancelOrder;
using LascodiaTradingEngine.Application.Orders.Commands.DeleteOrder;
using LascodiaTradingEngine.Application.Orders.Commands.ModifyOrder;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitOrder;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Orders;

public class OrderLifecycleTests
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly Mock<IEAOwnershipGuard> _mockOwnershipGuard;
    private readonly Mock<IIntegrationEventService> _mockEventService;

    public OrderLifecycleTests()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockOwnershipGuard = new Mock<IEAOwnershipGuard>();
        _mockEventService = new Mock<IIntegrationEventService>();
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(1L);
    }

    private void SetupOrders(List<Order> orders)
    {
        var mockSet = orders.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Order>()).Returns(mockSet.Object);
    }

    private void SetupEAInstances(List<EAInstance> instances)
    {
        var mockSet = instances.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EAInstance>()).Returns(mockSet.Object);
    }

    private void SetupEACommands()
    {
        var mockSet = new List<EACommand>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EACommand>()).Returns(mockSet.Object);
    }

    private static Order CreatePendingOrder(long id = 1, long accountId = 1) => new()
    {
        Id = id,
        Symbol = "EURUSD",
        OrderType = OrderType.Buy,
        ExecutionType = ExecutionType.Market,
        Status = OrderStatus.Pending,
        Price = 1.1000m,
        Quantity = 0.1m,
        StrategyId = 1,
        TradingAccountId = accountId,
        IsDeleted = false
    };

    // ========================================================================
    //  CancelOrderCommand
    // ========================================================================

    [Fact]
    public async Task Cancel_ShouldSucceed_WhenPendingOrderWithNoBrokerId()
    {
        var order = CreatePendingOrder();
        SetupOrders([order]);

        var handler = new CancelOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new CancelOrderCommand { Id = 1 }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public async Task Cancel_ShouldFail_WhenOrderNotFound()
    {
        SetupOrders([]);

        var handler = new CancelOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new CancelOrderCommand { Id = 999 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Cancel_ShouldFail_WhenOrderAlreadyFilled()
    {
        var order = CreatePendingOrder();
        order.Status = OrderStatus.Filled;
        SetupOrders([order]);

        var handler = new CancelOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new CancelOrderCommand { Id = 1 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Contains("cannot be cancelled", result.message);
    }

    [Fact]
    public async Task Cancel_ShouldFail_WhenUnauthorized()
    {
        var order = CreatePendingOrder(accountId: 99);
        SetupOrders([order]);

        var handler = new CancelOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new CancelOrderCommand { Id = 1 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Contains("Unauthorized", result.message);
    }

    [Fact]
    public async Task Cancel_ShouldAllow_SubmittedOrder()
    {
        var order = CreatePendingOrder();
        order.Status = OrderStatus.Submitted;
        SetupOrders([order]);

        var handler = new CancelOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new CancelOrderCommand { Id = 1 }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    // ========================================================================
    //  DeleteOrderCommand
    // ========================================================================

    [Fact]
    public async Task Delete_ShouldSoftDelete_ExistingOrder()
    {
        var order = CreatePendingOrder();
        SetupOrders([order]);

        var handler = new DeleteOrderCommandHandler(_mockWriteContext.Object, _mockEventService.Object);
        var result = await handler.Handle(new DeleteOrderCommand { Id = 1 }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.True(order.IsDeleted);
    }

    [Fact]
    public async Task Delete_ShouldFail_WhenOrderNotFound()
    {
        SetupOrders([]);

        var handler = new DeleteOrderCommandHandler(_mockWriteContext.Object, _mockEventService.Object);
        var result = await handler.Handle(new DeleteOrderCommand { Id = 999 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    // ========================================================================
    //  ModifyOrderCommand
    // ========================================================================

    [Fact]
    public async Task Modify_ShouldUpdate_StopLossAndTakeProfit()
    {
        var order = CreatePendingOrder();
        order.StopLoss = 1.0900m;
        order.TakeProfit = 1.1100m;
        SetupOrders([order]);

        var handler = new ModifyOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new ModifyOrderCommand
        {
            Id = 1, StopLoss = 1.0850m, TakeProfit = 1.1200m
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal(1.0850m, order.StopLoss);
        Assert.Equal(1.1200m, order.TakeProfit);
    }

    [Fact]
    public async Task Modify_ShouldFail_WhenOrderNotFound()
    {
        SetupOrders([]);

        var handler = new ModifyOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new ModifyOrderCommand { Id = 999, StopLoss = 1.0m }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Modify_ShouldFail_WhenUnauthorized()
    {
        var order = CreatePendingOrder(accountId: 99);
        SetupOrders([order]);

        var handler = new ModifyOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new ModifyOrderCommand { Id = 1, StopLoss = 1.0m }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Contains("Unauthorized", result.message);
    }

    [Fact]
    public async Task Modify_ShouldKeepExisting_WhenNewValueIsNull()
    {
        var order = CreatePendingOrder();
        order.StopLoss = 1.0900m;
        order.TakeProfit = 1.1100m;
        SetupOrders([order]);

        var handler = new ModifyOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new ModifyOrderCommand
        {
            Id = 1, StopLoss = null, TakeProfit = 1.1200m
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal(1.0900m, order.StopLoss); // unchanged
        Assert.Equal(1.1200m, order.TakeProfit); // updated
    }

    // ========================================================================
    //  SubmitOrderCommand
    // ========================================================================

    [Fact]
    public async Task Submit_ShouldFail_WhenOrderNotFound()
    {
        SetupOrders([]);

        var handler = new SubmitOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new SubmitOrderCommand { Id = 999 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Submit_ShouldFail_WhenNotPending()
    {
        var order = CreatePendingOrder();
        order.Status = OrderStatus.Filled;
        SetupOrders([order]);

        var handler = new SubmitOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new SubmitOrderCommand { Id = 1 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Contains("not in Pending", result.message);
    }

    [Fact]
    public async Task Submit_ShouldFail_WhenUnauthorized()
    {
        var order = CreatePendingOrder(accountId: 99);
        SetupOrders([order]);

        var handler = new SubmitOrderCommandHandler(_mockWriteContext.Object, _mockOwnershipGuard.Object);
        var result = await handler.Handle(new SubmitOrderCommand { Id = 1 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Contains("Unauthorized", result.message);
    }

    // ========================================================================
    //  Validators
    // ========================================================================

    [Fact]
    public void CancelValidator_ShouldFail_WhenIdIsZero()
    {
        var validator = new CancelOrderCommandValidator();
        var result = validator.TestValidate(new CancelOrderCommand { Id = 0 });
        result.ShouldHaveValidationErrorFor(c => c.Id);
    }

    [Fact]
    public void DeleteValidator_ShouldFail_WhenIdIsZero()
    {
        var validator = new DeleteOrderCommandValidator();
        var result = validator.TestValidate(new DeleteOrderCommand { Id = 0 });
        result.ShouldHaveValidationErrorFor(c => c.Id);
    }

    [Fact]
    public void ModifyValidator_ShouldFail_WhenStopLossNegative()
    {
        var validator = new ModifyOrderCommandValidator();
        var result = validator.TestValidate(new ModifyOrderCommand { Id = 1, StopLoss = -1m });
        result.ShouldHaveValidationErrorFor(c => c.StopLoss);
    }

    [Fact]
    public void SubmitValidator_ShouldPass_WithValidId()
    {
        var validator = new SubmitOrderCommandValidator();
        var result = validator.TestValidate(new SubmitOrderCommand { Id = 1 });
        result.ShouldNotHaveAnyValidationErrors();
    }
}
