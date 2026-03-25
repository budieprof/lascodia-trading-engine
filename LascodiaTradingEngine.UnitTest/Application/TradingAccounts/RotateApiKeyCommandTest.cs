using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.RotateApiKey;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.TradingAccounts;

public class RotateApiKeyCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly Mock<IEAOwnershipGuard> _mockOwnershipGuard;
    private readonly IConfiguration _configuration;
    private readonly RotateApiKeyCommandValidator _validator;
    private readonly RotateApiKeyCommandHandler _handler;

    private const string EncryptionKey = "test-secret-key-that-is-long-enough-for-aes256-gcm!!";

    public RotateApiKeyCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockOwnershipGuard = new Mock<IEAOwnershipGuard>();
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);

        var configData = new Dictionary<string, string?>
        {
            { "Encryption:Key", EncryptionKey },
            { "JwtSettings:SecretKey", EncryptionKey },
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        _validator = new RotateApiKeyCommandValidator();
        _handler = new RotateApiKeyCommandHandler(
            _mockWriteContext.Object,
            _configuration,
            _mockOwnershipGuard.Object);
    }

    private void SetupTradingAccounts(List<TradingAccount> accounts)
    {
        var mockSet = accounts.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(mockSet.Object);
    }

    private TradingAccount CreateTestAccount(long id = 1)
    {
        return new TradingAccount
        {
            Id                = id,
            AccountId         = "12345678",
            BrokerServer      = "MetaQuotes-Demo",
            BrokerName        = "MetaQuotes",
            AccountName       = "Account 12345678",
            Currency          = "USD",
            AccountType       = AccountType.Demo,
            EncryptedPassword = FieldEncryption.Encrypt("pw", EncryptionKey),
            EncryptedApiKey   = FieldEncryption.Encrypt("old-api-key", EncryptionKey),
            IsActive          = true,
            IsDeleted         = false
        };
    }

    // ── Handler Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RotateApiKey_ShouldSucceed_WhenOwner()
    {
        // Arrange
        var account = CreateTestAccount(id: 1);
        var oldEncryptedApiKey = account.EncryptedApiKey;
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(1L);

        var command = new RotateApiKeyCommand { Id = 1 };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("API key rotated successfully", result.message);
        Assert.NotNull(result.data);
        Assert.False(string.IsNullOrEmpty(result.data!.ApiKey));
        Assert.False(string.IsNullOrEmpty(result.data.EncryptedApiKeyBlob));
        Assert.Equal(64, result.data.ApiKey.Length);

        // Verify the entity's encrypted key was updated (no longer the old value)
        Assert.NotEqual(oldEncryptedApiKey, account.EncryptedApiKey);

        // Verify the new plain key decrypts correctly from the entity
        var storedKey = FieldEncryption.Decrypt(account.EncryptedApiKey!, EncryptionKey);
        Assert.Equal(result.data.ApiKey, storedKey);

        // Verify the encrypted blob decrypts to the same key
        var blobKey = FieldEncryption.Decrypt(result.data.EncryptedApiKeyBlob, EncryptionKey);
        Assert.Equal(result.data.ApiKey, blobKey);

        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RotateApiKey_ShouldFail_WhenNotOwner()
    {
        // Arrange — caller (99) is not the account owner (1)
        var account = CreateTestAccount(id: 1);
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(99L);

        var command = new RotateApiKeyCommand { Id = 1 };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Contains("Unauthorized", result.message);

        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RotateApiKey_ShouldFail_WhenAccountNotFound()
    {
        // Arrange — empty database
        SetupTradingAccounts(new List<TradingAccount>());

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(999L);

        var command = new RotateApiKeyCommand { Id = 999 };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Trading account not found", result.message);
    }

    [Fact]
    public async Task RotateApiKey_ShouldSucceed_WhenCallerAccountIdIsNull()
    {
        // Arrange — null caller (internal/service call) bypasses ownership check
        var account = CreateTestAccount(id: 5);
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns((long?)null);

        var command = new RotateApiKeyCommand { Id = 5 };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert — ownership guard returns null, so the check (null is not null) is false => pass
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        Assert.Equal(64, result.data!.ApiKey.Length);
    }

    [Fact]
    public async Task RotateApiKey_ShouldNotFindDeletedAccount()
    {
        // Arrange — account exists but is soft-deleted
        var account = CreateTestAccount(id: 1);
        account.IsDeleted = true;
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(1L);

        var command = new RotateApiKeyCommand { Id = 1 };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Trading account not found", result.message);
    }

    // ── Validator Tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task Validator_ShouldFail_WhenIdIsZero()
    {
        var command = new RotateApiKeyCommand { Id = 0 };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Id)
              .WithErrorMessage("Id must be greater than zero");
    }

    [Fact]
    public async Task Validator_ShouldFail_WhenIdIsNegative()
    {
        var command = new RotateApiKeyCommand { Id = -1 };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Id)
              .WithErrorMessage("Id must be greater than zero");
    }

    [Fact]
    public async Task Validator_ShouldPass_WithValidId()
    {
        var command = new RotateApiKeyCommand { Id = 1 };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
