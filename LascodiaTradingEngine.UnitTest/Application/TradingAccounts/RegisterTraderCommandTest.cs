using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.RegisterTrader;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.TradingAccounts;

public class RegisterTraderCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly IConfiguration _configuration;
    private readonly RegisterTraderCommandValidator _validator;
    private readonly RegisterTraderCommandHandler _handler;

    // A deterministic encryption key long enough for AES-256
    private const string EncryptionKey = "test-secret-key-that-is-long-enough-for-aes256-gcm!!";

    public RegisterTraderCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);

        var configData = new Dictionary<string, string?>
        {
            { "Encryption:Key", EncryptionKey },
            { "JwtSettings:SecretKey", EncryptionKey },
            { "JwtSettings:Issuer", "lascodia-trading-engine" },
            { "JwtSettings:Audience", "lascodia-trading-engine-api" },
            { "JwtSettings:ExpirationMinutes", "480" },
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        _validator = new RegisterTraderCommandValidator();
        _handler = new RegisterTraderCommandHandler(_mockWriteContext.Object, _configuration);
    }

    private void SetupTradingAccounts(List<TradingAccount> accounts)
    {
        var mockSet = accounts.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(mockSet.Object);
    }

    // ── Handler Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterTrader_ShouldSucceed_WhenNewAccountId()
    {
        // Arrange — no existing accounts
        SetupTradingAccounts(new List<TradingAccount>());

        var command = new RegisterTraderCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Currency     = "USD",
            Leverage     = 100,
            AccountType  = AccountType.Demo,
            IsPaper      = false
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        Assert.False(string.IsNullOrEmpty(result.data!.Token));
        Assert.False(string.IsNullOrEmpty(result.data.ApiKey));
        Assert.False(string.IsNullOrEmpty(result.data.EncryptedApiKeyBlob));
        Assert.Equal("Bearer", result.data.TokenType);
        Assert.Equal("12345678", result.data.Account.AccountId);
        Assert.Equal("MetaQuotes-Demo", result.data.Account.BrokerServer);
        Assert.Equal("MetaQuotes", result.data.Account.BrokerName);

        // Verify entity was added and saved
        _mockDbContext.Verify(
            c => c.Set<TradingAccount>().AddAsync(
                It.Is<TradingAccount>(e =>
                    e.AccountId == "12345678" &&
                    e.BrokerServer == "MetaQuotes-Demo" &&
                    e.IsActive == true &&
                    !string.IsNullOrEmpty(e.EncryptedPassword) &&
                    !string.IsNullOrEmpty(e.EncryptedApiKey)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RegisterTrader_ShouldReturnExistingToken_WhenAlreadyRegistered()
    {
        // Arrange — existing account with an API key already set
        var plainApiKey = FieldEncryption.GenerateRandomPassword(64);
        var encryptedApiKey = FieldEncryption.Encrypt(plainApiKey, EncryptionKey);

        var existing = new TradingAccount
        {
            Id               = 42,
            AccountId        = "12345678",
            BrokerServer     = "MetaQuotes-Demo",
            BrokerName       = "MetaQuotes",
            AccountName      = "Account 12345678",
            Currency         = "USD",
            AccountType      = AccountType.Demo,
            EncryptedPassword = FieldEncryption.Encrypt("some-password", EncryptionKey),
            EncryptedApiKey  = encryptedApiKey,
            IsActive         = true,
            IsDeleted        = false
        };

        SetupTradingAccounts(new List<TradingAccount> { existing });

        var command = new RegisterTraderCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert — should succeed and return existing token
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        Assert.False(string.IsNullOrEmpty(result.data!.Token));
        Assert.Equal(plainApiKey, result.data.ApiKey);
        Assert.Equal(42, result.data.Account.Id);

        // Verify NO new entity was added
        _mockDbContext.Verify(
            c => c.Set<TradingAccount>().AddAsync(
                It.IsAny<TradingAccount>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterTrader_ShouldGenerateLegacyApiKey_WhenExistingAccountHasNoApiKey()
    {
        // Arrange — existing account WITHOUT EncryptedApiKey (legacy)
        var existing = new TradingAccount
        {
            Id                = 10,
            AccountId         = "99999999",
            BrokerServer      = "ICMarkets-Demo",
            BrokerName        = "IC Markets",
            AccountName       = "Account 99999999",
            Currency          = "USD",
            EncryptedPassword = FieldEncryption.Encrypt("pw", EncryptionKey),
            EncryptedApiKey   = null, // legacy — no API key
            IsActive          = true,
            IsDeleted         = false
        };

        SetupTradingAccounts(new List<TradingAccount> { existing });

        var command = new RegisterTraderCommand
        {
            AccountId    = "99999999",
            BrokerServer = "ICMarkets-Demo",
            BrokerName   = "IC Markets"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert — should succeed and generate a new API key for the legacy account
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        Assert.False(string.IsNullOrEmpty(result.data!.ApiKey));

        // Verify SaveChangesAsync was called to persist the generated API key
        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Validator Tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task Validator_ShouldFail_WhenAccountIdIsEmpty()
    {
        var command = new RegisterTraderCommand
        {
            AccountId    = string.Empty,
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AccountId)
              .WithErrorMessage("AccountId cannot be empty");
    }

    [Fact]
    public async Task Validator_ShouldFail_WhenBrokerServerIsEmpty()
    {
        var command = new RegisterTraderCommand
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
    public async Task Validator_ShouldFail_WhenBrokerNameIsEmpty()
    {
        var command = new RegisterTraderCommand
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
    public async Task Validator_ShouldFail_WhenPasswordTooShort()
    {
        var command = new RegisterTraderCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Password     = "short"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Password)
              .WithErrorMessage("Password must be at least 8 characters");
    }

    [Fact]
    public async Task Validator_ShouldPass_WhenPasswordIsNull()
    {
        var command = new RegisterTraderCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Password     = null
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveValidationErrorFor(c => c.Password);
    }

    [Fact]
    public async Task Validator_ShouldPass_WithValidCommand()
    {
        var command = new RegisterTraderCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            BrokerName   = "MetaQuotes",
            Currency     = "USD",
            Leverage     = 100,
            Password     = "Valid@pass123"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
