using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.SignalFilters;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.SignalFilters;

public class PortfolioCorrelationCheckerTest
{
    private readonly Mock<IReadApplicationDbContext> _mockContext;
    private readonly Mock<DbContext> _mockDbContext;

    public PortfolioCorrelationCheckerTest()
    {
        _mockContext = new Mock<IReadApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
    }

    private void SetupPositions(List<Position> positions)
    {
        var mockSet = positions.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(mockSet.Object);
    }

    private PortfolioCorrelationChecker CreateChecker(string[][] groups)
    {
        var options = new CorrelationGroupOptions { Groups = groups };
        return new PortfolioCorrelationChecker(_mockContext.Object, options);
    }

    [Fact]
    public async Task IsCorrelationBreached_SymbolNotInAnyGroup_ReturnsFalse()
    {
        SetupPositions(new List<Position>());
        var checker = CreateChecker(new[] { new[] { "EURUSD", "GBPUSD" } });

        var result = await checker.IsCorrelationBreachedAsync("XAUUSD", "Long", 3, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsCorrelationBreached_NoOpenPositions_ReturnsFalse()
    {
        SetupPositions(new List<Position>());
        var checker = CreateChecker(new[] { new[] { "EURUSD", "GBPUSD", "AUDUSD" } });

        var result = await checker.IsCorrelationBreachedAsync("EURUSD", "Long", 2, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsCorrelationBreached_UnderLimit_ReturnsFalse()
    {
        SetupPositions(new List<Position>
        {
            new() { Id = 1, Symbol = "GBPUSD", Status = PositionStatus.Open, IsDeleted = false }
        });
        var checker = CreateChecker(new[] { new[] { "EURUSD", "GBPUSD", "AUDUSD" } });

        var result = await checker.IsCorrelationBreachedAsync("EURUSD", "Long", 3, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsCorrelationBreached_AtLimit_ReturnsTrue()
    {
        SetupPositions(new List<Position>
        {
            new() { Id = 1, Symbol = "EURUSD", Status = PositionStatus.Open, IsDeleted = false },
            new() { Id = 2, Symbol = "GBPUSD", Status = PositionStatus.Open, IsDeleted = false }
        });
        var checker = CreateChecker(new[] { new[] { "EURUSD", "GBPUSD", "AUDUSD" } });

        var result = await checker.IsCorrelationBreachedAsync("AUDUSD", "Long", 2, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsCorrelationBreached_ClosedPositionNotCounted_ReturnsFalse()
    {
        SetupPositions(new List<Position>
        {
            new() { Id = 1, Symbol = "EURUSD", Status = PositionStatus.Closed, IsDeleted = false },
            new() { Id = 2, Symbol = "GBPUSD", Status = PositionStatus.Open, IsDeleted = false }
        });
        var checker = CreateChecker(new[] { new[] { "EURUSD", "GBPUSD", "AUDUSD" } });

        var result = await checker.IsCorrelationBreachedAsync("AUDUSD", "Long", 2, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsCorrelationBreached_DeletedPositionNotCounted_ReturnsFalse()
    {
        SetupPositions(new List<Position>
        {
            new() { Id = 1, Symbol = "EURUSD", Status = PositionStatus.Open, IsDeleted = true },
            new() { Id = 2, Symbol = "GBPUSD", Status = PositionStatus.Open, IsDeleted = false }
        });
        var checker = CreateChecker(new[] { new[] { "EURUSD", "GBPUSD", "AUDUSD" } });

        var result = await checker.IsCorrelationBreachedAsync("AUDUSD", "Long", 2, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsCorrelationBreached_DifferentGroup_NotCounted()
    {
        SetupPositions(new List<Position>
        {
            new() { Id = 1, Symbol = "USDCHF", Status = PositionStatus.Open, IsDeleted = false },
            new() { Id = 2, Symbol = "USDJPY", Status = PositionStatus.Open, IsDeleted = false }
        });
        var checker = CreateChecker(new[]
        {
            new[] { "EURUSD", "GBPUSD" },
            new[] { "USDCHF", "USDJPY", "USDCAD" }
        });

        // EURUSD is in group 1; USDCHF/USDJPY are in group 2 — should not count
        var result = await checker.IsCorrelationBreachedAsync("EURUSD", "Long", 2, CancellationToken.None);

        Assert.False(result);
    }
}
