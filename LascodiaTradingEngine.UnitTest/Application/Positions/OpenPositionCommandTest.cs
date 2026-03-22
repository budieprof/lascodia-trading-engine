using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Commands.OpenPosition;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Positions;

public class OpenPositionCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IIntegrationEventService> _mockEventService;
    private readonly OpenPositionCommandHandler _handler;
    private readonly OpenPositionCommandValidator _validator;

    public OpenPositionCommandTest()
    {
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockEventService  = new Mock<IIntegrationEventService>();

        var mockDbContext = new Mock<DbContext>();
        var positions = new List<Position>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Position>()).Returns(positions.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new OpenPositionCommandHandler(_mockWriteContext.Object, _mockEventService.Object);
        _validator = new OpenPositionCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = string.Empty,
            Direction         = "Long",
            OpenLots          = 0.01m,
            AverageEntryPrice = 1.1000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Exceeds_Max_Length()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "EURUSDGBPJPY",
            Direction         = "Long",
            OpenLots          = 0.01m,
            AverageEntryPrice = 1.1000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot exceed 10 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Direction_Is_Empty()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "EURUSD",
            Direction         = string.Empty,
            OpenLots          = 0.01m,
            AverageEntryPrice = 1.1000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Direction)
              .WithErrorMessage("Direction cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Direction_Is_Invalid()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "EURUSD",
            Direction         = "Buy",
            OpenLots          = 0.01m,
            AverageEntryPrice = 1.1000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Direction)
              .WithErrorMessage("Direction must be 'Long' or 'Short'");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_OpenLots_Is_Zero()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "EURUSD",
            Direction         = "Long",
            OpenLots          = 0m,
            AverageEntryPrice = 1.1000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.OpenLots)
              .WithErrorMessage("OpenLots must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AverageEntryPrice_Is_Zero()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "EURUSD",
            Direction         = "Long",
            OpenLots          = 0.01m,
            AverageEntryPrice = 0m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AverageEntryPrice)
              .WithErrorMessage("AverageEntryPrice must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "EURUSD",
            Direction         = "Long",
            OpenLots          = 0.01m,
            AverageEntryPrice = 1.1000m,
            StopLoss          = 1.0950m,
            TakeProfit        = 1.1100m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Short_Direction()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "GBPUSD",
            Direction         = "Short",
            OpenLots          = 0.05m,
            AverageEntryPrice = 1.2500m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new OpenPositionCommand
        {
            Symbol            = "EURUSD",
            Direction         = "Long",
            OpenLots          = 0.01m,
            AverageEntryPrice = 1.1000m,
            StopLoss          = 1.0950m,
            TakeProfit        = 1.1100m,
            IsPaper           = true,
            OpenOrderId       = 42
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
