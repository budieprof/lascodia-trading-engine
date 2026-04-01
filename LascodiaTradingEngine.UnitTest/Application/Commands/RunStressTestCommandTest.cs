using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StressTest.Commands.RunStressTest;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Commands;

public class RunStressTestCommandTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IStressTestEngine> _mockStressEngine;
    private readonly Mock<DbContext> _mockReadDbContext;
    private readonly Mock<DbContext> _mockWriteDbContext;
    private readonly RunStressTestCommandHandler _handler;

    public RunStressTestCommandTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockStressEngine = new Mock<IStressTestEngine>();

        _mockReadDbContext = new Mock<DbContext>();
        _mockWriteDbContext = new Mock<DbContext>();

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockReadDbContext.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockWriteDbContext.Object);
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _handler = new RunStressTestCommandHandler(
            _mockReadContext.Object,
            _mockWriteContext.Object,
            _mockStressEngine.Object);
    }

    [Fact]
    public async Task Handle_ScenarioNotFound_ReturnsError()
    {
        var emptyScenarios = new List<StressTestScenario>().AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<StressTestScenario>()).Returns(emptyScenarios.Object);

        var command = new RunStressTestCommand { ScenarioId = 999, TradingAccountId = 1 };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Contains("Scenario not found", result.message);
    }

    [Fact]
    public async Task Handle_AccountNotFound_ReturnsError()
    {
        var scenario = new StressTestScenario
        {
            Id = 1,
            Name = "SNB De-Peg",
            ScenarioType = StressScenarioType.Historical,
            IsActive = true,
            IsDeleted = false
        };

        var scenarios = new List<StressTestScenario> { scenario }.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<StressTestScenario>()).Returns(scenarios.Object);

        var emptyAccounts = new List<TradingAccount>().AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<TradingAccount>()).Returns(emptyAccounts.Object);

        var command = new RunStressTestCommand { ScenarioId = 1, TradingAccountId = 999 };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Contains("Account not found", result.message);
    }

    [Fact]
    public async Task Handle_Success_PersistsResult()
    {
        var scenario = new StressTestScenario
        {
            Id = 1,
            Name = "Hypothetical Shock",
            ScenarioType = StressScenarioType.Hypothetical,
            IsActive = true,
            IsDeleted = false
        };

        var account = EntityFactory.CreateAccount(equity: 10000m);

        var scenarios = new List<StressTestScenario> { scenario }.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<StressTestScenario>()).Returns(scenarios.Object);

        var accounts = new List<TradingAccount> { account }.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<TradingAccount>()).Returns(accounts.Object);

        var positions = new List<Position>().AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<Position>()).Returns(positions.Object);

        var stressResult = new StressTestResult
        {
            Id = 42,
            StressTestScenarioId = 1,
            TradingAccountId = account.Id,
            PortfolioEquity = 10000m,
            StressedPnl = -500m,
            StressedPnlPct = -5.0m
        };

        _mockStressEngine
            .Setup(e => e.RunScenarioAsync(scenario, account, It.IsAny<IReadOnlyList<Position>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stressResult);

        var results = new List<StressTestResult>().AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<StressTestResult>()).Returns(results.Object);

        var command = new RunStressTestCommand { ScenarioId = 1, TradingAccountId = account.Id };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal(42L, result.data);
    }
}
