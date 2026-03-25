using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.UnitTest.Application.RateLimiting;

/// <summary>
/// Tests for FieldEncryption (AES-256-GCM) — covers the security utility
/// alongside the SystemHealth/MarketRegime query patterns.
/// </summary>
public class FieldEncryptionTest
{
    private const string TestKey = "test-encryption-key-for-unit-tests-32chars!!";

    [Fact]
    public void Encrypt_Decrypt_RoundTrip()
    {
        var original = "MySecretPassword123!";
        var encrypted = FieldEncryption.Encrypt(original, TestKey);
        var decrypted = FieldEncryption.Decrypt(encrypted, TestKey);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesEnc2Prefix()
    {
        var encrypted = FieldEncryption.Encrypt("test", TestKey);
        Assert.StartsWith("enc2:", encrypted);
    }

    [Fact]
    public void Encrypt_SameInput_DifferentOutput()
    {
        // Nonce is random, so two encryptions of the same value should differ
        var a = FieldEncryption.Encrypt("same-value", TestKey);
        var b = FieldEncryption.Encrypt("same-value", TestKey);
        Assert.NotEqual(a, b);

        // But both should decrypt to the same value
        Assert.Equal("same-value", FieldEncryption.Decrypt(a, TestKey));
        Assert.Equal("same-value", FieldEncryption.Decrypt(b, TestKey));
    }

    [Fact]
    public void Decrypt_PlainText_ReturnsAsIs()
    {
        // Non-encrypted values are returned unchanged (backwards compatibility)
        var plain = "not-encrypted-password";
        var result = FieldEncryption.Decrypt(plain, TestKey);
        Assert.Equal(plain, result);
    }

    [Fact]
    public void IsEncrypted_EncPrefix_ReturnsTrue()
    {
        Assert.True(FieldEncryption.IsEncrypted("enc:abc123"));
    }

    [Fact]
    public void IsEncrypted_PlainText_ReturnsFalse()
    {
        Assert.False(FieldEncryption.IsEncrypted("plain-text"));
    }

    [Fact]
    public void IsEncrypted_Null_ReturnsFalse()
    {
        Assert.False(FieldEncryption.IsEncrypted(null));
    }

    [Fact]
    public void GenerateRandomPassword_DefaultLength()
    {
        var password = FieldEncryption.GenerateRandomPassword();
        Assert.Equal(16, password.Length);
    }

    [Fact]
    public void GenerateRandomPassword_CustomLength()
    {
        var password = FieldEncryption.GenerateRandomPassword(32);
        Assert.Equal(32, password.Length);
    }

    [Fact]
    public void GenerateRandomPassword_UniqueEachTime()
    {
        var a = FieldEncryption.GenerateRandomPassword();
        var b = FieldEncryption.GenerateRandomPassword();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var encrypted = FieldEncryption.Encrypt("secret", TestKey);
        Assert.ThrowsAny<Exception>(() => FieldEncryption.Decrypt(encrypted, "wrong-key-that-is-different!!!!!!!!"));
    }
}
