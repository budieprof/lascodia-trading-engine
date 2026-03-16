using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Orders;

public class CreateOrderCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IIntegrationEventService> _mockEventService;
    private readonly CreateOrderCommandHandler _handler;
    private readonly CreateOrderCommandValidator _validator;

    public CreateOrderCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockEventService = new Mock<IIntegrationEventService>();

        var mockDbContext = new Mock<DbContext>();
        var orders = new List<Order>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler = new CreateOrderCommandHandler(
            _mockWriteContext.Object,
            _mockEventService.Object
        );

        _validator = new CreateOrderCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new CreateOrderCommand
        {
            Symbol = string.Empty,
            OrderType = "Buy",
            Quantity = 1,
            Price = 100
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_OrderType_Is_Invalid()
    {
        var command = new CreateOrderCommand
        {
            Symbol = "BTC/USDT",
            OrderType = "Hold",
            Quantity = 1,
            Price = 100
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.OrderType)
              .WithErrorMessage("OrderType must be 'Buy' or 'Sell'");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Quantity_Is_Zero()
    {
        var command = new CreateOrderCommand
        {
            Symbol = "BTC/USDT",
            OrderType = "Buy",
            Quantity = 0,
            Price = 100
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Quantity)
              .WithErrorMessage("Quantity must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateOrderCommand
        {
            Symbol = "BTC/USDT",
            OrderType = "Buy",
            Quantity = 1.5m,
            Price = 50000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateOrderCommand
        {
            BusinessId = 1,
            Symbol = "BTC/USDT",
            OrderType = "Buy",
            Quantity = 1.5m,
            Price = 50000m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
