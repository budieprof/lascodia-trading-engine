using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.UnitTest.Application.Security;

public class FieldEncryptionTest
{
    private const string EncryptionKey = "test-encryption-key-32-characters";

    [Fact]
    public void Encrypt_and_decrypt_should_roundtrip()
    {
        var plainText = "my-secret-password";
        var encrypted = FieldEncryption.Encrypt(plainText, EncryptionKey);
        var decrypted = FieldEncryption.Decrypt(encrypted, EncryptionKey);

        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void Encrypted_value_should_have_enc2_prefix()
    {
        var encrypted = FieldEncryption.Encrypt("test", EncryptionKey);
        Assert.StartsWith("enc2:", encrypted);
    }

    [Fact]
    public void IsEncrypted_should_detect_prefix()
    {
        Assert.True(FieldEncryption.IsEncrypted("enc:base64data"));   // v1
        Assert.True(FieldEncryption.IsEncrypted("enc2:base64data"));  // v2
        Assert.False(FieldEncryption.IsEncrypted("plain-text"));
        Assert.False(FieldEncryption.IsEncrypted(null));
    }

    [Fact]
    public void Decrypt_plain_text_should_return_as_is()
    {
        var plainText = "not-encrypted";
        var result = FieldEncryption.Decrypt(plainText, EncryptionKey);
        Assert.Equal(plainText, result);
    }

    [Fact]
    public void Different_keys_should_fail_decryption()
    {
        var encrypted = FieldEncryption.Encrypt("secret", EncryptionKey);
        Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => FieldEncryption.Decrypt(encrypted, "different-key-entirely-wrong!!"));
    }

    [Fact]
    public void Two_encryptions_of_same_plaintext_should_produce_different_ciphertexts()
    {
        var encrypted1 = FieldEncryption.Encrypt("test", EncryptionKey);
        var encrypted2 = FieldEncryption.Encrypt("test", EncryptionKey);
        Assert.NotEqual(encrypted1, encrypted2); // random nonce
    }

    [Fact]
    public void GenerateRandomPassword_should_return_correct_length()
    {
        var password = FieldEncryption.GenerateRandomPassword(20);
        Assert.Equal(20, password.Length);
    }

    [Fact]
    public void GenerateRandomPassword_default_should_be_16_chars()
    {
        var password = FieldEncryption.GenerateRandomPassword();
        Assert.Equal(16, password.Length);
    }
}
