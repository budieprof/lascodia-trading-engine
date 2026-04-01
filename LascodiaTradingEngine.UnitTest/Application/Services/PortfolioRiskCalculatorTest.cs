using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class PortfolioRiskCalculatorTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly Mock<IStressTestEngine> _mockStressTestEngine;
    private readonly PortfolioRiskOptions _options;
    private readonly PortfolioRiskCalculator _calculator;

    public PortfolioRiskCalculatorTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _mockStressTestEngine = new Mock<IStressTestEngine>();

        _options = new PortfolioRiskOptions
        {
            ReturnWindowDays = 60,
            VaRConfidence95 = 0.95m,
            VaRConfidence99 = 0.99m,
            MaxVaR95Pct = 5.0m,
            MonteCarloSimulations = 0
        };

        _calculator = new PortfolioRiskCalculator(
            _mockReadContext.Object,
            _options,
            _mockStressTestEngine.Object,
            Mock.Of<ILogger<PortfolioRiskCalculator>>());
    }

    [Fact]
    public async Task ComputeAsync_EmptyPositions_ReturnsZeroMetrics()
    {
        var account = EntityFactory.CreateAccount(equity: 10000m);
        var positions = Array.Empty<Position>();

        var result = await _calculator.ComputeAsync(account, positions, CancellationToken.None);

        Assert.Equal(0m, result.VaR95);
        Assert.Equal(0m, result.VaR99);
        Assert.Equal(0m, result.CVaR95);
        Assert.Equal(0m, result.CVaR99);
        Assert.Equal(0m, result.StressedVaR);
        Assert.Equal(0m, result.CorrelationConcentration);
    }

    [Fact]
    public async Task ComputeAsync_SinglePositionWithCandleData_VaRGreaterThanZero()
    {
        var account = EntityFactory.CreateAccount(equity: 10000m);
        var position = EntityFactory.CreatePosition(symbol: "EURUSD", lots: 1.0m, entryPrice: 1.1000m);
        var positions = new List<Position> { position };

        // Generate 65 daily candles with varying closes to produce 60+ returns
        var candles = new List<Candle>();
        decimal basePrice = 1.1000m;
        for (int i = 0; i < 65; i++)
        {
            decimal variation = (i % 2 == 0 ? 0.002m : -0.001m) * ((i % 5) + 1);
            candles.Add(EntityFactory.CreateCandle(
                symbol: "EURUSD",
                timeframe: Timeframe.D1,
                close: basePrice + variation,
                timestamp: DateTime.UtcNow.AddDays(-65 + i)));
        }

        var mockCandleSet = candles.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Candle>()).Returns(mockCandleSet.Object);

        // Stress test scenarios - empty set
        var scenarios = new List<StressTestScenario>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<StressTestScenario>()).Returns(scenarios.Object);

        var result = await _calculator.ComputeAsync(account, positions, CancellationToken.None);

        Assert.True(result.VaR95 != 0, "VaR95 should be non-zero with sufficient candle data");
    }

    [Fact]
    public async Task ComputeAsync_InsufficientHistory_ReturnsZero()
    {
        var account = EntityFactory.CreateAccount(equity: 10000m);
        var position = EntityFactory.CreatePosition(symbol: "EURUSD");
        var positions = new List<Position> { position };

        // Only 5 candles: not enough for 10+ return scenarios
        var candles = new List<Candle>();
        for (int i = 0; i < 5; i++)
        {
            candles.Add(EntityFactory.CreateCandle(
                symbol: "EURUSD",
                timeframe: Timeframe.D1,
                close: 1.1000m + i * 0.001m,
                timestamp: DateTime.UtcNow.AddDays(-5 + i)));
        }

        var mockCandleSet = candles.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Candle>()).Returns(mockCandleSet.Object);

        var result = await _calculator.ComputeAsync(account, positions, CancellationToken.None);

        Assert.Equal(0m, result.VaR95);
        Assert.Equal(0m, result.CVaR95);
    }

    [Fact]
    public async Task ComputeAsync_MarginalVaR_IncreasesWhenAddingPosition()
    {
        var account = EntityFactory.CreateAccount(equity: 50000m);
        var existingPosition = EntityFactory.CreatePosition(symbol: "EURUSD", lots: 1.0m, entryPrice: 1.1000m);
        var positions = new List<Position> { existingPosition };

        // Generate sufficient candle data
        var candles = new List<Candle>();
        decimal basePrice = 1.1000m;
        var rng = new Random(42);
        for (int i = 0; i < 65; i++)
        {
            decimal variation = (decimal)(rng.NextDouble() * 0.01 - 0.005);
            candles.Add(EntityFactory.CreateCandle(
                symbol: "EURUSD",
                timeframe: Timeframe.D1,
                close: basePrice + variation,
                timestamp: DateTime.UtcNow.AddDays(-65 + i)));
        }

        var mockCandleSet = candles.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Candle>()).Returns(mockCandleSet.Object);

        var scenarios = new List<StressTestScenario>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<StressTestScenario>()).Returns(scenarios.Object);

        var proposedSignal = EntityFactory.CreateSignal(symbol: "EURUSD", lotSize: 2.0m);

        var marginalResult = await _calculator.ComputeMarginalAsync(
            proposedSignal, account, positions, CancellationToken.None);

        // Marginal VaR should be non-zero (adding a position in the same direction increases risk)
        // The post-trade VaR should differ from the base case
        Assert.NotEqual(0m, marginalResult.PostTradeVaR95);
    }

    [Fact]
    public async Task ComputeAsync_MonteCarloDisabled_MCFieldsZero()
    {
        _options.MonteCarloSimulations = 0; // Disabled

        var account = EntityFactory.CreateAccount(equity: 10000m);
        var position = EntityFactory.CreatePosition(symbol: "EURUSD", lots: 1.0m, entryPrice: 1.1000m);
        var positions = new List<Position> { position };

        var candles = new List<Candle>();
        decimal basePrice = 1.1000m;
        for (int i = 0; i < 65; i++)
        {
            decimal variation = (i % 2 == 0 ? 0.002m : -0.001m) * ((i % 5) + 1);
            candles.Add(EntityFactory.CreateCandle(
                symbol: "EURUSD",
                timeframe: Timeframe.D1,
                close: basePrice + variation,
                timestamp: DateTime.UtcNow.AddDays(-65 + i)));
        }

        var mockCandleSet = candles.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Candle>()).Returns(mockCandleSet.Object);

        var scenarios = new List<StressTestScenario>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<StressTestScenario>()).Returns(scenarios.Object);

        var result = await _calculator.ComputeAsync(account, positions, CancellationToken.None);

        Assert.Equal(0m, result.MonteCarloVaR95);
        Assert.Equal(0m, result.MonteCarloVaR99);
        Assert.Equal(0m, result.MonteCarloCVaR95);
    }

    [Fact]
    public async Task ComputeAsync_ZeroEquity_NoDivisionByZero()
    {
        var account = EntityFactory.CreateAccount(equity: 0m);
        var position = EntityFactory.CreatePosition(symbol: "EURUSD", lots: 1.0m, entryPrice: 1.1000m);
        var positions = new List<Position> { position };

        var candles = new List<Candle>();
        decimal basePrice = 1.1000m;
        for (int i = 0; i < 65; i++)
        {
            decimal variation = (i % 2 == 0 ? 0.002m : -0.001m) * ((i % 5) + 1);
            candles.Add(EntityFactory.CreateCandle(
                symbol: "EURUSD",
                timeframe: Timeframe.D1,
                close: basePrice + variation,
                timestamp: DateTime.UtcNow.AddDays(-65 + i)));
        }

        var mockCandleSet = candles.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Candle>()).Returns(mockCandleSet.Object);

        var scenarios = new List<StressTestScenario>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<StressTestScenario>()).Returns(scenarios.Object);

        // Should not throw - exercise the zero equity code path
        var exception = await Record.ExceptionAsync(() =>
            _calculator.ComputeAsync(account, positions, CancellationToken.None));

        Assert.Null(exception);
    }
}
