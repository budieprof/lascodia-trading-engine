using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Alerts.Commands.CreateAlert;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Alerts;

public class CreateAlertCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly CreateAlertCommandHandler _handler;
    private readonly CreateAlertCommandValidator _validator;

    public CreateAlertCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var alerts = new List<Alert>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Alert>()).Returns(alerts.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new CreateAlertCommandHandler(_mockWriteContext.Object);
        _validator = new CreateAlertCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AlertType_Is_Empty()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = string.Empty,
            Symbol      = "EURUSD",
            Channel     = "Email",
            Destination = "user@example.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AlertType)
              .WithErrorMessage("AlertType is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AlertType_Is_Invalid()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = "InvalidType",
            Symbol      = "EURUSD",
            Channel     = "Email",
            Destination = "user@example.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AlertType)
              .WithErrorMessage("AlertType must be one of: PriceLevel, DrawdownBreached, SignalGenerated, OrderFilled, PositionClosed, MLModelDegraded");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = "PriceLevel",
            Symbol      = string.Empty,
            Channel     = "Email",
            Destination = "user@example.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Exceeds_Max_Length()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = "PriceLevel",
            Symbol      = "EURUSDGBPJPY",
            Channel     = "Email",
            Destination = "user@example.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot exceed 10 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Channel_Is_Invalid()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = "PriceLevel",
            Symbol      = "EURUSD",
            Channel     = "SMS",
            Destination = "+1234567890"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Channel)
              .WithErrorMessage("Channel must be one of: Email, Webhook, Telegram");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Destination_Is_Empty()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = "PriceLevel",
            Symbol      = "EURUSD",
            Channel     = "Email",
            Destination = string.Empty
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Destination)
              .WithErrorMessage("Destination is required");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = "PriceLevel",
            Symbol      = "EURUSD",
            Channel     = "Webhook",
            Destination = "https://hooks.example.com/alert"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateAlertCommand
        {
            AlertType   = "PriceLevel",
            Symbol      = "EURUSD",
            Channel     = "Telegram",
            Destination = "123456789"
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
