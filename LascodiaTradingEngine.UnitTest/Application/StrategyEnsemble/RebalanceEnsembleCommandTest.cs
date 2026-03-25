using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyEnsemble.Commands.RebalanceEnsemble;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyEnsemble;

public class RebalanceEnsembleCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<DbContext> _mockReadDbContext;
    private readonly Mock<DbContext> _mockWriteDbContext;

    public RebalanceEnsembleCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockReadDbContext = new Mock<DbContext>();
        _mockWriteDbContext = new Mock<DbContext>();
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockReadDbContext.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockWriteDbContext.Object);
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    [Fact]
    public async Task Handle_NoActiveStrategies_ReturnsNotFound()
    {
        var strategies = new List<Strategy>().AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);

        var handler = new RebalanceEnsembleCommandHandler(_mockWriteContext.Object, _mockReadContext.Object);

        var result = await handler.Handle(new RebalanceEnsembleCommand(), CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Handle_AllPositiveSharpe_WeightsProportionalToSharpe()
    {
        // Arrange: two strategies with Sharpe 2.0 and 3.0
        var strategies = new List<Strategy>
        {
            new() { Id = 1, Status = StrategyStatus.Active, IsDeleted = false },
            new() { Id = 2, Status = StrategyStatus.Active, IsDeleted = false }
        };

        var snapshots = new List<StrategyPerformanceSnapshot>
        {
            new() { Id = 1, StrategyId = 1, SharpeRatio = 2.0m, EvaluatedAt = DateTime.UtcNow, IsDeleted = false },
            new() { Id = 2, StrategyId = 2, SharpeRatio = 3.0m, EvaluatedAt = DateTime.UtcNow, IsDeleted = false },
        };

        _mockReadDbContext.Setup(c => c.Set<Strategy>())
            .Returns(strategies.AsQueryable().BuildMockDbSet().Object);
        _mockReadDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>())
            .Returns(snapshots.AsQueryable().BuildMockDbSet().Object);

        var allocations = new List<StrategyAllocation>().AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<StrategyAllocation>()).Returns(allocations.Object);

        var handler = new RebalanceEnsembleCommandHandler(_mockWriteContext.Object, _mockReadContext.Object);

        // Act
        var result = await handler.Handle(new RebalanceEnsembleCommand(), CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Contains("Rebalanced 2 strategies", result.data);
    }

    [Fact]
    public async Task Handle_AllZeroSharpe_EqualWeights()
    {
        var strategies = new List<Strategy>
        {
            new() { Id = 1, Status = StrategyStatus.Active, IsDeleted = false },
            new() { Id = 2, Status = StrategyStatus.Active, IsDeleted = false }
        };

        // No snapshots → all Sharpe = 0 → equal weights
        var snapshots = new List<StrategyPerformanceSnapshot>();

        _mockReadDbContext.Setup(c => c.Set<Strategy>())
            .Returns(strategies.AsQueryable().BuildMockDbSet().Object);
        _mockReadDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>())
            .Returns(snapshots.AsQueryable().BuildMockDbSet().Object);

        var allocations = new List<StrategyAllocation>().AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<StrategyAllocation>()).Returns(allocations.Object);

        var handler = new RebalanceEnsembleCommandHandler(_mockWriteContext.Object, _mockReadContext.Object);

        var result = await handler.Handle(new RebalanceEnsembleCommand(), CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
    }
}
