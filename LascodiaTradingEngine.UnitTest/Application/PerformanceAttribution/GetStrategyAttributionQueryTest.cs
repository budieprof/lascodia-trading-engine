using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.PerformanceAttribution.Queries.GetStrategyAttribution;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.PerformanceAttribution;

public class GetStrategyAttributionQueryTest
{
    private readonly Mock<IReadApplicationDbContext> _mockContext;
    private readonly Mock<DbContext> _mockDbContext;

    public GetStrategyAttributionQueryTest()
    {
        _mockContext = new Mock<IReadApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
    }

    [Fact]
    public async Task Handle_StrategyNotFound_ReturnsNotFound()
    {
        _mockDbContext.Setup(c => c.Set<Strategy>())
            .Returns(new List<Strategy>().AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>())
            .Returns(new List<StrategyPerformanceSnapshot>().AsQueryable().BuildMockDbSet().Object);

        var handler = new GetStrategyAttributionQueryHandler(_mockContext.Object);

        var result = await handler.Handle(
            new GetStrategyAttributionQuery { StrategyId = 999 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Handle_NoPerformanceData_ReturnsNotFound()
    {
        var strategies = new List<Strategy>
        {
            new() { Id = 1, Name = "Test", Status = StrategyStatus.Active, IsDeleted = false }
        };
        _mockDbContext.Setup(c => c.Set<Strategy>())
            .Returns(strategies.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>())
            .Returns(new List<StrategyPerformanceSnapshot>().AsQueryable().BuildMockDbSet().Object);

        var handler = new GetStrategyAttributionQueryHandler(_mockContext.Object);

        var result = await handler.Handle(
            new GetStrategyAttributionQuery { StrategyId = 1 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Handle_WithData_ReturnsDto()
    {
        var strategies = new List<Strategy>
        {
            new() { Id = 1, Name = "MA Crossover", Status = StrategyStatus.Active, IsDeleted = false }
        };
        var snapshots = new List<StrategyPerformanceSnapshot>
        {
            new() { Id = 1, StrategyId = 1, WinRate = 0.6m, TotalPnL = 500m, SharpeRatio = 1.5m,
                     MaxDrawdownPct = 5m, WindowTrades = 50, EvaluatedAt = DateTime.UtcNow, IsDeleted = false }
        };
        _mockDbContext.Setup(c => c.Set<Strategy>())
            .Returns(strategies.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>())
            .Returns(snapshots.AsQueryable().BuildMockDbSet().Object);

        var handler = new GetStrategyAttributionQueryHandler(_mockContext.Object);

        var result = await handler.Handle(
            new GetStrategyAttributionQuery { StrategyId = 1 }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("MA Crossover", result.data.StrategyName);
        Assert.Equal(0.6m, result.data.WinRate);
        Assert.Equal(10m, result.data.AveragePnLPerTrade); // 500/50
    }
}
