using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Commands.RunBacktest;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Backtesting;

public class RunBacktestCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly RunBacktestCommandHandler _handler;
    private readonly RunBacktestCommandValidator _validator;

    public RunBacktestCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var queuedRuns = new List<BacktestRun>();
        var runs = queuedRuns.AsQueryable().BuildMockDbSet();
        runs.Setup(set => set.AddAsync(It.IsAny<BacktestRun>(), It.IsAny<CancellationToken>()))
            .Callback<BacktestRun, CancellationToken>((run, _) => queuedRuns.Add(run))
            .Returns<BacktestRun, CancellationToken>((_, _) =>
                new ValueTask<EntityEntry<BacktestRun>>((EntityEntry<BacktestRun>)null!));

        var strategies = new List<Strategy>
        {
            new()
            {
                Id = 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"mode":"baseline"}""",
                IsDeleted = false,
            }
        }.AsQueryable().BuildMockDbSet();

        mockDbContext.Setup(c => c.Set<BacktestRun>()).Returns(runs.Object);
        mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var settingsProvider = new ValidationSettingsProvider();
        var optionsBuilder = new BacktestOptionsSnapshotBuilder(
            settingsProvider,
            NullLogger<BacktestOptionsSnapshotBuilder>.Instance);
        var runFactory = new ValidationRunFactory(optionsBuilder, TimeProvider.System);
        _handler = new RunBacktestCommandHandler(_mockWriteContext.Object, runFactory);
        _validator = new RunBacktestCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_StrategyId_Is_Zero()
    {
        var command = new RunBacktestCommand
        {
            StrategyId = 0,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-6),
            ToDate = DateTime.UtcNow,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.StrategyId)
              .WithErrorMessage("StrategyId must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new RunBacktestCommand
        {
            StrategyId = 1,
            Symbol = string.Empty,
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-6),
            ToDate = DateTime.UtcNow,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Timeframe_Is_Invalid()
    {
        var command = new RunBacktestCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "W1",
            FromDate = DateTime.UtcNow.AddMonths(-6),
            ToDate = DateTime.UtcNow,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Timeframe)
              .WithErrorMessage("Timeframe must be one of: M1, M5, M15, H1, H4, D1");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_InitialBalance_Is_Zero()
    {
        var command = new RunBacktestCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-6),
            ToDate = DateTime.UtcNow,
            InitialBalance = 0m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.InitialBalance)
              .WithErrorMessage("InitialBalance must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_ToDate_Is_Before_FromDate()
    {
        var command = new RunBacktestCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow,
            ToDate = DateTime.UtcNow.AddMonths(-6),
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.ToDate)
              .WithErrorMessage("ToDate must be after FromDate");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new RunBacktestCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-6),
            ToDate = DateTime.UtcNow,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success_With_Queued_Message()
    {
        var command = new RunBacktestCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-6),
            ToDate = DateTime.UtcNow,
            InitialBalance = 10000m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Backtest queued successfully", result.message);
    }
}
