using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Queries.GetLatestCandle;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.MarketData;

public class GetCandlesQueryTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IMapper> _mockMapper;

    public GetCandlesQueryTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockMapper = new Mock<IMapper>();
    }

    [Fact]
    public async Task Handler_Should_Return_Latest_Candle_When_Found()
    {
        // Arrange
        var olderCandle = new Candle
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Open = 1.1000m,
            High = 1.1050m,
            Low = 1.0950m,
            Close = 1.1020m,
            Volume = 5000m,
            Timestamp = DateTime.UtcNow.AddHours(-2),
            IsClosed = true,
            IsDeleted = false
        };

        var newerCandle = new Candle
        {
            Id = 2,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Open = 1.1020m,
            High = 1.1080m,
            Low = 1.1000m,
            Close = 1.1060m,
            Volume = 4500m,
            Timestamp = DateTime.UtcNow.AddHours(-1),
            IsClosed = true,
            IsDeleted = false
        };

        var candles = new List<Candle> { olderCandle, newerCandle }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Candle>()).Returns(candles.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var expectedDto = new CandleDto
        {
            Id = 2,
            Symbol = "EURUSD",
            Open = 1.1020m,
            High = 1.1080m,
            Low = 1.1000m,
            Close = 1.1060m,
            Volume = 4500m
        };

        _mockMapper.Setup(m => m.Map<CandleDto>(It.IsAny<Candle>())).Returns(expectedDto);

        var handler = new GetLatestCandleQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetLatestCandleQuery { Symbol = "EURUSD", Timeframe = "H1" };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
        Assert.NotNull(result.data);
        Assert.Equal(2, result.data!.Id);
    }

    [Fact]
    public async Task Handler_Should_Return_NotFound_When_No_Candles_Exist()
    {
        // Arrange
        var candles = new List<Candle>().AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Candle>()).Returns(candles.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetLatestCandleQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetLatestCandleQuery { Symbol = "EURUSD", Timeframe = "H1" };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("No candle found", result.message);
        Assert.Null(result.data);
    }

    [Fact]
    public async Task Handler_Should_Not_Return_Deleted_Candles()
    {
        // Arrange
        var deletedCandle = new Candle
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Open = 1.1000m,
            High = 1.1050m,
            Low = 1.0950m,
            Close = 1.1020m,
            Timestamp = DateTime.UtcNow.AddHours(-1),
            IsDeleted = true
        };

        var candles = new List<Candle> { deletedCandle }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Candle>()).Returns(candles.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetLatestCandleQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetLatestCandleQuery { Symbol = "EURUSD", Timeframe = "H1" };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("No candle found", result.message);
    }

    [Fact]
    public async Task Handler_Should_Not_Return_Candles_For_Different_Symbol()
    {
        // Arrange
        var candle = new Candle
        {
            Id = 1,
            Symbol = "GBPUSD",
            Timeframe = Timeframe.H1,
            Open = 1.2500m,
            High = 1.2550m,
            Low = 1.2450m,
            Close = 1.2520m,
            Timestamp = DateTime.UtcNow.AddHours(-1),
            IsDeleted = false
        };

        var candles = new List<Candle> { candle }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<Candle>()).Returns(candles.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var handler = new GetLatestCandleQueryHandler(_mockReadContext.Object, _mockMapper.Object);
        var query = new GetLatestCandleQuery { Symbol = "EURUSD", Timeframe = "H1" };

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }
}
