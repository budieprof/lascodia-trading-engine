using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.WalkForward.Commands.RunWalkForward;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.WalkForward;

public class RunWalkForwardCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly RunWalkForwardCommandHandler _handler;
    private readonly RunWalkForwardCommandValidator _validator;

    public RunWalkForwardCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var queuedRuns = new List<WalkForwardRun>();
        var runs = queuedRuns.AsQueryable().BuildMockDbSet();
        runs.Setup(set => set.AddAsync(It.IsAny<WalkForwardRun>(), It.IsAny<CancellationToken>()))
            .Callback<WalkForwardRun, CancellationToken>((run, _) => queuedRuns.Add(run))
            .Returns<WalkForwardRun, CancellationToken>((_, _) =>
                new ValueTask<EntityEntry<WalkForwardRun>>((EntityEntry<WalkForwardRun>)null!));

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

        mockDbContext.Setup(c => c.Set<WalkForwardRun>()).Returns(runs.Object);
        mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var settingsProvider = new ValidationSettingsProvider();
        var optionsBuilder = new BacktestOptionsSnapshotBuilder(
            settingsProvider,
            NullLogger<BacktestOptionsSnapshotBuilder>.Instance);
        var snapshotBuilder = new StrategyExecutionSnapshotBuilder();
        var runFactory = new ValidationRunFactory(optionsBuilder, snapshotBuilder, TimeProvider.System);
        _handler = new RunWalkForwardCommandHandler(_mockWriteContext.Object, runFactory);
        _validator = new RunWalkForwardCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_StrategyId_Is_Zero()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 0,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 90,
            OutOfSampleDays = 30,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.StrategyId)
              .WithErrorMessage("StrategyId must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 1,
            Symbol = string.Empty,
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 90,
            OutOfSampleDays = 30,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Timeframe_Is_Empty()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = string.Empty,
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 90,
            OutOfSampleDays = 30,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Timeframe)
              .WithErrorMessage("Timeframe cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_InSampleDays_Is_Zero()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 0,
            OutOfSampleDays = 30,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.InSampleDays)
              .WithErrorMessage("InSampleDays must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_OutOfSampleDays_Is_Zero()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 90,
            OutOfSampleDays = 0,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.OutOfSampleDays)
              .WithErrorMessage("OutOfSampleDays must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_InitialBalance_Is_Zero()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 90,
            OutOfSampleDays = 30,
            InitialBalance = 0m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.InitialBalance)
              .WithErrorMessage("InitialBalance must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 90,
            OutOfSampleDays = 30,
            InitialBalance = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new RunWalkForwardCommand
        {
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = "H1",
            FromDate = DateTime.UtcNow.AddMonths(-12),
            ToDate = DateTime.UtcNow,
            InSampleDays = 90,
            OutOfSampleDays = 30,
            InitialBalance = 10000m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
