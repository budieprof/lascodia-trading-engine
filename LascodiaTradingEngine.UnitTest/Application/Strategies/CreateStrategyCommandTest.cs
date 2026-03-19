using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Commands.CreateStrategy;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

public class CreateStrategyCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly CreateStrategyCommandHandler _handler;
    private readonly CreateStrategyCommandValidator _validator;

    public CreateStrategyCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var strategies = new List<Strategy>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new CreateStrategyCommandHandler(_mockWriteContext.Object);
        _validator = new CreateStrategyCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Name_Is_Empty()
    {
        var command = new CreateStrategyCommand
        {
            Name         = string.Empty,
            Description  = "Test description",
            StrategyType = "MovingAverageCrossover",
            Symbol       = "EURUSD",
            Timeframe    = "H1"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Name)
              .WithErrorMessage("Name cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_StrategyType_Is_Invalid()
    {
        var command = new CreateStrategyCommand
        {
            Name         = "My Strategy",
            Description  = "Test description",
            StrategyType = "InvalidType",
            Symbol       = "EURUSD",
            Timeframe    = "H1"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.StrategyType)
              .WithErrorMessage("StrategyType must be MovingAverageCrossover, RSIReversion, BreakoutScalper, or Custom");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Timeframe_Is_Invalid()
    {
        var command = new CreateStrategyCommand
        {
            Name         = "My Strategy",
            Description  = "Test description",
            StrategyType = "MovingAverageCrossover",
            Symbol       = "EURUSD",
            Timeframe    = "W1"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Timeframe)
              .WithErrorMessage("Timeframe must be M1, M5, M15, H1, H4, or D1");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateStrategyCommand
        {
            Name         = "MA Crossover Strategy",
            Description  = "A moving average crossover strategy",
            StrategyType = "MovingAverageCrossover",
            Symbol       = "EURUSD",
            Timeframe    = "H1"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateStrategyCommand
        {
            Name         = "MA Crossover Strategy",
            Description  = "A moving average crossover strategy",
            StrategyType = "MovingAverageCrossover",
            Symbol       = "EURUSD",
            Timeframe    = "H1"
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
