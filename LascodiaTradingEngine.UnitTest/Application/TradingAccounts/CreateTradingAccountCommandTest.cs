using FluentValidation.TestHelper;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.CreateTradingAccount;

namespace LascodiaTradingEngine.UnitTest.Application.TradingAccounts;

public class CreateTradingAccountCommandTest
{
    private readonly CreateTradingAccountCommandValidator _validator;

    public CreateTradingAccountCommandTest()
    {
        _validator = new CreateTradingAccountCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AccountId_Is_Empty()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId   = string.Empty,
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AccountId)
              .WithErrorMessage("AccountId cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AccountId_Exceeds_Max_Length()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = new string('X', 101),
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AccountId)
              .WithErrorMessage("AccountId cannot exceed 100 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_BrokerServer_Is_Empty()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = string.Empty,
            BrokerName   = "MetaQuotes"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BrokerServer)
              .WithErrorMessage("BrokerServer cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_BrokerName_Is_Empty()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = string.Empty
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BrokerName)
              .WithErrorMessage("BrokerName cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Currency_Exceeds_Max_Length()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Currency     = "USDT"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Currency)
              .WithErrorMessage("Currency cannot exceed 3 characters");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Currency     = "USD",
            Leverage     = 100,
            IsPaper      = true
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Leverage_Is_Zero()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Leverage     = 0
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Leverage)
              .WithErrorMessage("Leverage must be greater than 0");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Leverage_Exceeds_Regulatory_Ceiling()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Leverage     = 1000
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Leverage)
              .WithErrorMessage("Leverage cannot exceed 500:1 (regulatory ceiling)");
    }

    [Fact]
    public async Task Validator_Should_Pass_When_Leverage_Is_At_Ceiling()
    {
        var command = new CreateTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Leverage     = 500
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveValidationErrorFor(c => c.Leverage);
    }
}
