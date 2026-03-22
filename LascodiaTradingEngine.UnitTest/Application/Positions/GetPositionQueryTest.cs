using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Queries.GetPosition;
using LascodiaTradingEngine.Application.Positions.Queries.DTOs;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Positions;

public class GetPositionQueryTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IMapper> _mockMapper;

    public GetPositionQueryTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockMapper = new Mock<IMapper>();
    }

    [Fact]
    public async Task Handler_Should_Return_Position_When_Found()
    {
        // Arrange
        var position = new Position
        {
            Id = 1,
            Symbol = "GBPUSD",
            Direction = PositionDirection.Long,
            OpenLots = 0.05m,
            AverageEntryPrice = 1.2500m,
            IsDeleted = false
        };

        var positions = new List<Position> { position }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Position>()).Returns(positions.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var expectedDto = new PositionDto
        {
            Id = 1,
            Symbol = "GBPUSD",
            Direction = PositionDirection.Long,
            OpenLots = 0.05m,
            AverageEntryPrice = 1.2500m
        };

        _mockMapper.Setup(m => m.Map<PositionDto>(It.IsAny<Position>())).Returns(expectedDto);

        var handler = new GetPositionQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetPositionQuery { Id = 1 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
        Assert.NotNull(result.data);
        Assert.Equal(1, result.data!.Id);
        Assert.Equal("GBPUSD", result.data.Symbol);
        Assert.Equal(PositionDirection.Long, result.data.Direction);
    }

    [Fact]
    public async Task Handler_Should_Return_NotFound_When_Position_Does_Not_Exist()
    {
        // Arrange
        var positions = new List<Position>().AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Position>()).Returns(positions.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetPositionQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetPositionQuery { Id = 999 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Position not found", result.message);
        Assert.Null(result.data);
    }

    [Fact]
    public async Task Handler_Should_Return_NotFound_When_Position_Is_Soft_Deleted()
    {
        // Arrange
        var position = new Position
        {
            Id = 1,
            Symbol = "GBPUSD",
            Direction = PositionDirection.Long,
            IsDeleted = true
        };

        var positions = new List<Position> { position }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Position>()).Returns(positions.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetPositionQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetPositionQuery { Id = 1 };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Position not found", result.message);
    }
}
