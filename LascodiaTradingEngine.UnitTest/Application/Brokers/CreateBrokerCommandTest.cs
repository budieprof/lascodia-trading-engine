using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Brokers.Commands.CreateBroker;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Brokers;

public class CreateBrokerCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly CreateBrokerCommandHandler _handler;
    private readonly CreateBrokerCommandValidator _validator;

    public CreateBrokerCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var brokers = new List<Broker>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Broker>()).Returns(brokers.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new CreateBrokerCommandHandler(_mockWriteContext.Object);
        _validator = new CreateBrokerCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Name_Is_Empty()
    {
        var command = new CreateBrokerCommand
        {
            Name       = string.Empty,
            BrokerType = "Oanda",
            BaseUrl    = "https://api.oanda.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Name)
              .WithErrorMessage("Name cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Name_Exceeds_Max_Length()
    {
        var command = new CreateBrokerCommand
        {
            Name       = new string('A', 101),
            BrokerType = "Oanda",
            BaseUrl    = "https://api.oanda.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Name)
              .WithErrorMessage("Name cannot exceed 100 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_BrokerType_Is_Empty()
    {
        var command = new CreateBrokerCommand
        {
            Name       = "My Broker",
            BrokerType = string.Empty,
            BaseUrl    = "https://api.oanda.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BrokerType)
              .WithErrorMessage("BrokerType cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_BrokerType_Is_Invalid()
    {
        var command = new CreateBrokerCommand
        {
            Name       = "My Broker",
            BrokerType = "InvalidBroker",
            BaseUrl    = "https://api.oanda.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BrokerType)
              .WithErrorMessage("BrokerType must be one of: Oanda, IB, Paper");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Environment_Is_Invalid()
    {
        var command = new CreateBrokerCommand
        {
            Name        = "My Broker",
            BrokerType  = "Oanda",
            Environment = "Staging",
            BaseUrl     = "https://api.oanda.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Environment)
              .WithErrorMessage("Environment must be one of: Live, Practice");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_BaseUrl_Is_Empty()
    {
        var command = new CreateBrokerCommand
        {
            Name       = "My Broker",
            BrokerType = "Oanda",
            BaseUrl    = string.Empty
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BaseUrl)
              .WithErrorMessage("BaseUrl cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateBrokerCommand
        {
            Name        = "Oanda Practice",
            BrokerType  = "Oanda",
            Environment = "Practice",
            BaseUrl     = "https://api-fxpractice.oanda.com"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateBrokerCommand
        {
            Name        = "Oanda Practice",
            BrokerType  = "Oanda",
            Environment = "Practice",
            BaseUrl     = "https://api-fxpractice.oanda.com",
            ApiKey      = "test-key",
            IsPaper     = true
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
