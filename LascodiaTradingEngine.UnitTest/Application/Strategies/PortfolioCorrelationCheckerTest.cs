using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.SignalFilters;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

public class PortfolioCorrelationCheckerTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<DbContext> _mockDbContext;

    public PortfolioCorrelationCheckerTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockDbContext   = new Mock<DbContext>();

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
    }

    private void SetupPositions(List<Position> positions)
    {
        var mockSet = positions.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(mockSet.Object);
    }

    private PortfolioCorrelationChecker CreateChecker(CorrelationGroupOptions? options = null)
    {
        options ??= new CorrelationGroupOptions();
        return new PortfolioCorrelationChecker(_mockReadContext.Object, options);
    }

    private static Position CreateOpenPosition(string symbol, long id = 0)
    {
        return new Position
        {
            Id        = id,
            Symbol    = symbol,
            Status    = PositionStatus.Open,
            Direction = PositionDirection.Long,
            OpenLots  = 0.10m,
            IsDeleted = false
        };
    }

    // ── Symbol in correlation group with positions >= max → breached ────────

    [Fact]
    public async Task IsCorrelationBreachedAsync_OpenPositionsAtMax_ReturnsTrue()
    {
        // Default groups include ["EURUSD", "GBPUSD", "AUDUSD", "NZDUSD"]
        // Set up 2 open positions in that group
        var positions = new List<Position>
        {
            CreateOpenPosition("EURUSD", 1),
            CreateOpenPosition("GBPUSD", 2)
        };
        SetupPositions(positions);

        var checker = CreateChecker();

        // maxCorrelatedPositions = 2, and we have 2 open → breached
        var result = await checker.IsCorrelationBreachedAsync(
            "AUDUSD", "Long", maxCorrelatedPositions: 2, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsCorrelationBreachedAsync_OpenPositionsExceedMax_ReturnsTrue()
    {
        var positions = new List<Position>
        {
            CreateOpenPosition("EURUSD", 1),
            CreateOpenPosition("GBPUSD", 2),
            CreateOpenPosition("NZDUSD", 3)
        };
        SetupPositions(positions);

        var checker = CreateChecker();

        // maxCorrelatedPositions = 2, we have 3 → breached
        var result = await checker.IsCorrelationBreachedAsync(
            "AUDUSD", "Long", maxCorrelatedPositions: 2, CancellationToken.None);

        Assert.True(result);
    }

    // ── Symbol in group but positions below max → not breached ─────────────

    [Fact]
    public async Task IsCorrelationBreachedAsync_OpenPositionsBelowMax_ReturnsFalse()
    {
        var positions = new List<Position>
        {
            CreateOpenPosition("EURUSD", 1)
        };
        SetupPositions(positions);

        var checker = CreateChecker();

        // maxCorrelatedPositions = 3, only 1 open → not breached
        var result = await checker.IsCorrelationBreachedAsync(
            "GBPUSD", "Long", maxCorrelatedPositions: 3, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsCorrelationBreachedAsync_NoOpenPositionsInGroup_ReturnsFalse()
    {
        SetupPositions(new List<Position>());

        var checker = CreateChecker();

        var result = await checker.IsCorrelationBreachedAsync(
            "EURUSD", "Long", maxCorrelatedPositions: 2, CancellationToken.None);

        Assert.False(result);
    }

    // ── Symbol not in any correlation group → not breached ─────────────────

    [Fact]
    public async Task IsCorrelationBreachedAsync_SymbolNotInAnyGroup_ReturnsFalse()
    {
        var positions = new List<Position>
        {
            CreateOpenPosition("EURUSD", 1),
            CreateOpenPosition("GBPUSD", 2),
            CreateOpenPosition("AUDUSD", 3)
        };
        SetupPositions(positions);

        var checker = CreateChecker();

        // "XAUUSD" is not in any default correlation group
        var result = await checker.IsCorrelationBreachedAsync(
            "XAUUSD", "Long", maxCorrelatedPositions: 1, CancellationToken.None);

        Assert.False(result);
    }

    // ── Custom correlation groups via options ──────────────────────────────

    [Fact]
    public async Task IsCorrelationBreachedAsync_CustomGroups_UsesConfiguredGroups()
    {
        var customOptions = new CorrelationGroupOptions
        {
            Groups =
            [
                ["XAUUSD", "XAGUSD", "XPTUSD"],
                ["BTCUSD", "ETHUSD"]
            ]
        };

        var positions = new List<Position>
        {
            CreateOpenPosition("XAUUSD", 1),
            CreateOpenPosition("XAGUSD", 2)
        };
        SetupPositions(positions);

        var checker = CreateChecker(customOptions);

        // XPTUSD is in the custom metals group with 2 open positions → breached at max=2
        var result = await checker.IsCorrelationBreachedAsync(
            "XPTUSD", "Long", maxCorrelatedPositions: 2, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsCorrelationBreachedAsync_CustomGroups_SymbolNotInCustomGroup_ReturnsFalse()
    {
        var customOptions = new CorrelationGroupOptions
        {
            Groups =
            [
                ["XAUUSD", "XAGUSD"]
            ]
        };

        var positions = new List<Position>
        {
            CreateOpenPosition("XAUUSD", 1),
            CreateOpenPosition("XAGUSD", 2)
        };
        SetupPositions(positions);

        var checker = CreateChecker(customOptions);

        // EURUSD is NOT in the custom group (default groups are replaced)
        var result = await checker.IsCorrelationBreachedAsync(
            "EURUSD", "Long", maxCorrelatedPositions: 1, CancellationToken.None);

        Assert.False(result);
    }

    // ── Deleted positions are excluded ─────────────────────────────────────

    [Fact]
    public async Task IsCorrelationBreachedAsync_DeletedPositionsNotCounted()
    {
        var positions = new List<Position>
        {
            CreateOpenPosition("EURUSD", 1),
            new Position
            {
                Id        = 2,
                Symbol    = "GBPUSD",
                Status    = PositionStatus.Open,
                Direction = PositionDirection.Long,
                OpenLots  = 0.10m,
                IsDeleted = true  // soft-deleted
            }
        };
        SetupPositions(positions);

        var checker = CreateChecker();

        // Only 1 non-deleted open position → below max of 2
        var result = await checker.IsCorrelationBreachedAsync(
            "AUDUSD", "Long", maxCorrelatedPositions: 2, CancellationToken.None);

        Assert.False(result);
    }

    // ── Closed positions are excluded ──────────────────────────────────────

    [Fact]
    public async Task IsCorrelationBreachedAsync_ClosedPositionsNotCounted()
    {
        var positions = new List<Position>
        {
            CreateOpenPosition("EURUSD", 1),
            new Position
            {
                Id        = 2,
                Symbol    = "GBPUSD",
                Status    = PositionStatus.Closed,
                Direction = PositionDirection.Long,
                OpenLots  = 0m,
                IsDeleted = false
            }
        };
        SetupPositions(positions);

        var checker = CreateChecker();

        // Only 1 open position → below max of 2
        var result = await checker.IsCorrelationBreachedAsync(
            "AUDUSD", "Long", maxCorrelatedPositions: 2, CancellationToken.None);

        Assert.False(result);
    }

    // ── Case insensitivity for symbol lookup ───────────────────────────────

    [Fact]
    public async Task IsCorrelationBreachedAsync_SymbolLookupIsCaseInsensitive()
    {
        var positions = new List<Position>
        {
            CreateOpenPosition("EURUSD", 1),
            CreateOpenPosition("GBPUSD", 2)
        };
        SetupPositions(positions);

        var checker = CreateChecker();

        // Pass lowercase — the checker upper-cases before group lookup
        var result = await checker.IsCorrelationBreachedAsync(
            "audusd", "Long", maxCorrelatedPositions: 2, CancellationToken.None);

        Assert.True(result);
    }
}
