using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.ChangePassword;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.TradingAccounts;

public class ChangePasswordCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly Mock<IEAOwnershipGuard> _mockOwnershipGuard;
    private readonly IConfiguration _configuration;
    private readonly ChangePasswordCommandValidator _validator;
    private readonly ChangePasswordCommandHandler _handler;

    private const string EncryptionKey = "test-secret-key-that-is-long-enough-for-aes256-gcm!!";

    public ChangePasswordCommandTest()
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

        _validator = new ChangePasswordCommandValidator();
        _handler = new ChangePasswordCommandHandler(
            _mockWriteContext.Object,
            _configuration,
            _mockOwnershipGuard.Object);
    }

    private void SetupTradingAccounts(List<TradingAccount> accounts)
    {
        var mockSet = accounts.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(mockSet.Object);
    }

    private TradingAccount CreateTestAccount(long id = 1, string password = "currentpassword")
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
            EncryptedPassword = FieldEncryption.Encrypt(password, EncryptionKey),
            IsActive          = true,
            IsDeleted         = false
        };
    }

    // ── Handler Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_ShouldSucceed_WithCorrectCurrentPassword()
    {
        // Arrange
        var account = CreateTestAccount(id: 1, password: "oldPassword123");
        SetupTradingAccounts(new List<TradingAccount> { account });

        // Caller is the owner
        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(1L);

        var command = new ChangePasswordCommand
        {
            Id              = 1,
            CurrentPassword = "oldPassword123",
            NewPassword     = "newPassword456"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Password changed successfully", result.message);

        // Verify the password was actually updated on the entity
        var updatedPassword = FieldEncryption.Decrypt(account.EncryptedPassword, EncryptionKey);
        Assert.Equal("newPassword456", updatedPassword);

        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_ShouldFail_WithoutCurrentPassword()
    {
        // Arrange — current password is now mandatory
        var account = CreateTestAccount(id: 1, password: "oldPassword123");
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(1L);

        var command = new ChangePasswordCommand
        {
            Id              = 1,
            CurrentPassword = null,
            NewPassword     = "brandNewPassword"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal("Current password is required to change password", result.message);

        // Verify password was NOT changed
        var storedPassword = FieldEncryption.Decrypt(account.EncryptedPassword, EncryptionKey);
        Assert.Equal("oldPassword123", storedPassword);
    }

    [Fact]
    public async Task ChangePassword_ShouldFail_WithWrongCurrentPassword()
    {
        // Arrange
        var account = CreateTestAccount(id: 1, password: "correctPassword");
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(1L);

        var command = new ChangePasswordCommand
        {
            Id              = 1,
            CurrentPassword = "wrongPassword",
            NewPassword     = "newPassword456"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal("Current password is incorrect", result.message);

        // Verify password was NOT changed
        var storedPassword = FieldEncryption.Decrypt(account.EncryptedPassword, EncryptionKey);
        Assert.Equal("correctPassword", storedPassword);

        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_ShouldFail_WhenUnauthorized()
    {
        // Arrange — caller's account ID (99) does not match request ID (1)
        var account = CreateTestAccount(id: 1, password: "pw");
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(99L);

        var command = new ChangePasswordCommand
        {
            Id          = 1,
            NewPassword = "newPassword456"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Contains("Unauthorized", result.message);

        // Verify no DB call was made
        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_ShouldFail_WhenAccountNotFound()
    {
        // Arrange — empty database
        SetupTradingAccounts(new List<TradingAccount>());

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns(999L);

        var command = new ChangePasswordCommand
        {
            Id          = 999,
            NewPassword = "newPassword456"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
        Assert.Equal("Trading account not found", result.message);
    }

    [Fact]
    public async Task ChangePassword_ShouldSucceed_WhenCallerAccountIdIsNull_WithCurrentPassword()
    {
        // Arrange — null caller (e.g. internal/service call) should bypass ownership check
        var account = CreateTestAccount(id: 1, password: "pw");
        SetupTradingAccounts(new List<TradingAccount> { account });

        _mockOwnershipGuard.Setup(g => g.GetCallerAccountId()).Returns((long?)null);

        var command = new ChangePasswordCommand
        {
            Id              = 1,
            CurrentPassword = "pw",
            NewPassword     = "newPassword789"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert — ownership guard returns null, so the check (null is not null) is false => pass
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
    }

    // ── Validator Tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task Validator_ShouldFail_WhenNewPasswordIsEmpty()
    {
        var command = new ChangePasswordCommand
        {
            Id          = 1,
            NewPassword = string.Empty
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.NewPassword)
              .WithErrorMessage("NewPassword cannot be empty");
    }

    [Fact]
    public async Task Validator_ShouldFail_WhenNewPasswordTooShort()
    {
        var command = new ChangePasswordCommand
        {
            Id          = 1,
            NewPassword = "short"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.NewPassword)
              .WithErrorMessage("NewPassword must be at least 8 characters");
    }

    [Fact]
    public async Task Validator_ShouldFail_WhenNewPasswordExceedsMaxLength()
    {
        var command = new ChangePasswordCommand
        {
            Id          = 1,
            NewPassword = new string('X', 129)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.NewPassword)
              .WithErrorMessage("NewPassword cannot exceed 128 characters");
    }

    [Fact]
    public async Task Validator_ShouldPass_WithValidCommand()
    {
        var command = new ChangePasswordCommand
        {
            Id          = 1,
            NewPassword = "ValidNew@123"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
