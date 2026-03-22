using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Queries.GetOrder;
using LascodiaTradingEngine.Application.Orders.Queries.DTOs;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Orders;

public class GetOrderQueryTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IMapper> _mockMapper;

    public GetOrderQueryTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockMapper = new Mock<IMapper>();
    }

    [Fact]
    public async Task Handler_Should_Return_Order_When_Found()
    {
        // Arrange
        var order = new Order
        {
            Id = 1,
            Symbol = "EURUSD",
            OrderType = OrderType.Buy,
            Quantity = 0.01m,
            Price = 1.1000m,
            IsDeleted = false
        };

        var orders = new List<Order> { order }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var expectedDto = new OrderDto
        {
            Id = 1,
            Symbol = "EURUSD",
            OrderType = OrderType.Buy,
            Quantity = 0.01m,
            Price = 1.1000m
        };

        _mockMapper.Setup(m => m.Map<OrderDto>(It.IsAny<Order>())).Returns(expectedDto);

        var handler = new GetOrderQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetOrderQuery { Id = 1 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
        Assert.NotNull(result.data);
        Assert.Equal(1, result.data!.Id);
        Assert.Equal("EURUSD", result.data.Symbol);
    }

    [Fact]
    public async Task Handler_Should_Return_NotFound_When_Order_Does_Not_Exist()
    {
        // Arrange
        var orders = new List<Order>().AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetOrderQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetOrderQuery { Id = 999 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Order not found", result.message);
        Assert.Null(result.data);
    }

    [Fact]
    public async Task Handler_Should_Return_NotFound_When_Order_Is_Soft_Deleted()
    {
        // Arrange
        var order = new Order
        {
            Id = 1,
            Symbol = "EURUSD",
            OrderType = OrderType.Buy,
            Quantity = 0.01m,
            Price = 1.1000m,
            IsDeleted = true
        };

        var orders = new List<Order> { order }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetOrderQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetOrderQuery { Id = 1 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Order not found", result.message);
    }
}
