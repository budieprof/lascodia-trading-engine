using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MockQueryable.Moq;
using Microsoft.Extensions.Options;
using LascodiaTradingEngine.Application.Bridge.Options;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.LoginTradingAccount;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.TradingAccounts;

public class LoginTradingAccountCommandTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly IConfiguration _configuration;
    private readonly LoginTradingAccountCommandValidator _validator;
    private readonly LoginTradingAccountCommandHandler _handler;

    private const string EncryptionKey = "test-secret-key-that-is-long-enough-for-aes256-gcm!!";

    public LoginTradingAccountCommandTest()
    {
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);

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

        _validator = new LoginTradingAccountCommandValidator();
        _handler = new LoginTradingAccountCommandHandler(_mockReadContext.Object, _configuration, Options.Create(new BridgeOptions()));
    }

    private void SetupTradingAccounts(List<TradingAccount> accounts)
    {
        var mockSet = accounts.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(mockSet.Object);
    }

    private TradingAccount CreateTestAccount(string password = "correctpassword", string? apiKey = null)
    {
        var encryptedPassword = FieldEncryption.Encrypt(password, EncryptionKey);
        string? encryptedApiKey = apiKey is not null
            ? FieldEncryption.Encrypt(apiKey, EncryptionKey)
            : null;

        return new TradingAccount
        {
            Id                = 1,
            AccountId         = "12345678",
            BrokerServer      = "MetaQuotes-Demo",
            BrokerName        = "MetaQuotes",
            AccountName       = "Account 12345678",
            Currency          = "USD",
            AccountType       = AccountType.Demo,
            EncryptedPassword = encryptedPassword,
            EncryptedApiKey   = encryptedApiKey,
            IsActive          = true,
            IsDeleted         = false
        };
    }

    // ── Handler Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ShouldSucceed_WithValidPassword()
    {
        // Arrange
        var account = CreateTestAccount(password: "mySecurePassword1");
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            Password     = "mySecurePassword1",
            LoginSource  = "web"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        Assert.False(string.IsNullOrEmpty(result.data!.Token));
        Assert.Equal("Bearer", result.data.TokenType);
        Assert.Equal(1, result.data.Account.Id);
        Assert.Equal("12345678", result.data.Account.AccountId);
    }

    [Fact]
    public async Task Login_ShouldSucceed_WithValidApiKey()
    {
        // Arrange
        var plainApiKey = "my-ea-api-key-that-is-64-characters-long-1234567890abcdefghijklmn";
        var account = CreateTestAccount(password: "pw", apiKey: plainApiKey);
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            ApiKey       = plainApiKey,
            LoginSource  = "ea"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        Assert.False(string.IsNullOrEmpty(result.data!.Token));
        Assert.Equal(1, result.data.Account.Id);
    }

    [Fact]
    public async Task Login_ShouldSucceed_WithEncryptedApiKeyBlob()
    {
        // Arrange
        var plainApiKey = "my-ea-api-key-that-is-64-characters-long-1234567890abcdefghijklmn";
        var account = CreateTestAccount(password: "pw", apiKey: plainApiKey);
        SetupTradingAccounts(new List<TradingAccount> { account });

        // Simulate the EA sending the encrypted blob it received at registration
        var encryptedBlob = FieldEncryption.Encrypt(plainApiKey, EncryptionKey);

        var command = new LoginTradingAccountCommand
        {
            AccountId           = "12345678",
            BrokerServer        = "MetaQuotes-Demo",
            EncryptedApiKeyBlob = encryptedBlob,
            LoginSource         = "ea"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        Assert.False(string.IsNullOrEmpty(result.data!.Token));
    }

    [Fact]
    public async Task Login_ShouldFail_WithWrongPassword()
    {
        // Arrange
        var account = CreateTestAccount(password: "correctpassword");
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            Password     = "wrongpassword",
            LoginSource  = "web"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal("Invalid credentials", result.message);
        Assert.Null(result.data);
    }

    [Fact]
    public async Task Login_ShouldFail_WhenAccountNotFound()
    {
        // Arrange — empty database
        SetupTradingAccounts(new List<TradingAccount>());

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "nonexistent",
            BrokerServer = "MetaQuotes-Demo",
            Password     = "anypassword",
            LoginSource  = "web"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Trading account not found", result.message);
        Assert.Null(result.data);
    }

    [Fact]
    public async Task Login_ShouldFail_WhenAccountIsDeactivated()
    {
        // Arrange
        var account = CreateTestAccount(password: "mypassword");
        account.IsActive = false;
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            Password     = "mypassword",
            LoginSource  = "web"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal("Account is deactivated", result.message);
    }

    [Fact]
    public async Task Login_ShouldFail_WhenWebLoginMissingPassword()
    {
        // Arrange
        var account = CreateTestAccount(password: "pw");
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            Password     = null,
            LoginSource  = "web"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal("Password is required for web login", result.message);
    }

    [Fact]
    public async Task Login_ShouldFail_WhenEALoginMissingApiKeyAndBlob()
    {
        // Arrange
        var plainApiKey = "some-api-key-64-characters-long-for-testing-purposes-1234567890ab";
        var account = CreateTestAccount(password: "pw", apiKey: plainApiKey);
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            ApiKey       = null,
            EncryptedApiKeyBlob = null,
            LoginSource  = "ea"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal("ApiKey or EncryptedApiKeyBlob is required for EA login", result.message);
    }

    [Fact]
    public async Task Login_ShouldFail_WhenEALoginWithWrongApiKey()
    {
        // Arrange
        var plainApiKey = "correct-api-key-64-characters-long-for-testing-1234567890abcdefgh";
        var account = CreateTestAccount(password: "pw", apiKey: plainApiKey);
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            ApiKey       = "wrong-api-key-that-does-not-match-stored-value-at-all-0000000000",
            LoginSource  = "ea"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal("Invalid API key", result.message);
    }

    [Fact]
    public async Task Login_ShouldFail_WhenAccountHasNoApiKey()
    {
        // Arrange — account without EncryptedApiKey (legacy)
        var account = CreateTestAccount(password: "pw", apiKey: null);
        account.EncryptedApiKey = null;
        SetupTradingAccounts(new List<TradingAccount> { account });

        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            ApiKey       = "some-key",
            LoginSource  = "ea"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Contains("Account has no API key", result.message);
    }

    // ── Validator Tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task Validator_ShouldFail_WhenAccountIdIsEmpty()
    {
        var command = new LoginTradingAccountCommand
        {
            AccountId    = string.Empty,
            BrokerServer = "MetaQuotes-Demo",
            LoginSource  = "web"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.AccountId)
              .WithErrorMessage("AccountId cannot be empty");
    }

    [Fact]
    public async Task Validator_ShouldFail_WhenBrokerServerIsEmpty()
    {
        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = string.Empty,
            LoginSource  = "web"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BrokerServer)
              .WithErrorMessage("BrokerServer cannot be empty");
    }

    [Fact]
    public async Task Validator_ShouldFail_WhenLoginSourceIsInvalid()
    {
        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            LoginSource  = "mobile"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.LoginSource)
              .WithErrorMessage("LoginSource must be 'web' or 'ea'");
    }

    [Fact]
    public async Task Validator_ShouldPass_WithValidWebLogin()
    {
        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            Password     = "mypassword",
            LoginSource  = "web"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Validator_ShouldPass_WithValidEALogin()
    {
        var command = new LoginTradingAccountCommand
        {
            AccountId    = "12345678",
            BrokerServer = "MetaQuotes-Demo",
            ApiKey       = "some-api-key",
            LoginSource  = "ea"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
