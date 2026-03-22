using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.CreateTradingAccount;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.TradingAccounts;

public class CreateTradingAccountCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly CreateTradingAccountCommandHandler _handler;
    private readonly CreateTradingAccountCommandValidator _validator;

    public CreateTradingAccountCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var accounts = new List<TradingAccount>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(accounts.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new CreateTradingAccountCommandHandler(_mockWriteContext.Object);
        _validator = new CreateTradingAccountCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_BrokerId_Is_Zero()
    {
        var command = new CreateTradingAccountCommand
        {
            BrokerId    = 0,
            AccountId   = "001-001-12345-001",
            AccountName = "Practice Account"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BrokerId)
              .WithErrorMessage("BrokerId must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AccountId_Is_Empty()
    {
        var command = new CreateTradingAccountCommand
        {
            BrokerId    = 1,
            AccountId   = string.Empty,
            AccountName = "Practice Account"
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
            BrokerId    = 1,
            AccountId   = new string('X', 101),
            AccountName = "Practice Account"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AccountId)
              .WithErrorMessage("AccountId cannot exceed 100 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AccountName_Is_Empty()
    {
        var command = new CreateTradingAccountCommand
        {
            BrokerId    = 1,
            AccountId   = "001-001-12345-001",
            AccountName = string.Empty
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AccountName)
              .WithErrorMessage("AccountName cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_AccountName_Exceeds_Max_Length()
    {
        var command = new CreateTradingAccountCommand
        {
            BrokerId    = 1,
            AccountId   = "001-001-12345-001",
            AccountName = new string('A', 201)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AccountName)
              .WithErrorMessage("AccountName cannot exceed 200 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Currency_Exceeds_Max_Length()
    {
        var command = new CreateTradingAccountCommand
        {
            BrokerId    = 1,
            AccountId   = "001-001-12345-001",
            AccountName = "Practice Account",
            Currency    = "USDT"
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
            BrokerId    = 1,
            AccountId   = "001-001-12345-001",
            AccountName = "Practice Account",
            Currency    = "USD",
            IsPaper     = true
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateTradingAccountCommand
        {
            BrokerId    = 1,
            AccountId   = "001-001-12345-001",
            AccountName = "Practice Account",
            Currency    = "USD",
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
