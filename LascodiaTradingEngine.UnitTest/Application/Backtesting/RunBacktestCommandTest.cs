using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Commands.RunBacktest;
using LascodiaTradingEngine.Domain.Entities;

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
        var runs = new List<BacktestRun>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<BacktestRun>()).Returns(runs.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler = new RunBacktestCommandHandler(_mockWriteContext.Object);
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
