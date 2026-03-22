using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Queries.GetStrategy;
using LascodiaTradingEngine.Application.Strategies.Queries.DTOs;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

public class GetStrategyQueryTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IMapper> _mockMapper;

    public GetStrategyQueryTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockMapper = new Mock<IMapper>();
    }

    [Fact]
    public async Task Handler_Should_Return_Strategy_When_Found()
    {
        // Arrange
        var strategy = new Strategy
        {
            Id = 1,
            Name = "EUR/USD MA Cross",
            Description = "Moving average crossover strategy",
            StrategyType = StrategyType.MovingAverageCrossover,
            IsDeleted = false
        };

        var strategies = new List<Strategy> { strategy }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var expectedDto = new StrategyDto
        {
            Id = 1,
            Name = "EUR/USD MA Cross",
            Description = "Moving average crossover strategy",
            StrategyType = StrategyType.MovingAverageCrossover
        };

        _mockMapper.Setup(m => m.Map<StrategyDto>(It.IsAny<Strategy>())).Returns(expectedDto);

        var handler = new GetStrategyQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetStrategyQuery { Id = 1 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
        Assert.NotNull(result.data);
        Assert.Equal(1, result.data!.Id);
        Assert.Equal("EUR/USD MA Cross", result.data.Name);
    }

    [Fact]
    public async Task Handler_Should_Return_NotFound_When_Strategy_Does_Not_Exist()
    {
        // Arrange
        var strategies = new List<Strategy>().AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetStrategyQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetStrategyQuery { Id = 999 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Strategy not found", result.message);
        Assert.Null(result.data);
    }

    [Fact]
    public async Task Handler_Should_Return_NotFound_When_Strategy_Is_Soft_Deleted()
    {
        // Arrange
        var strategy = new Strategy
        {
            Id = 1,
            Name = "Deleted Strategy",
            IsDeleted = true
        };

        var strategies = new List<Strategy> { strategy }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetStrategyQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetStrategyQuery { Id = 1 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Strategy not found", result.message);
    }
}
