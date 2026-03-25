using System.Security.Cryptography;
using System.Text;

namespace LascodiaTradingEngine.Application.Common.Security;

/// <summary>
/// Encrypts and decrypts sensitive field values at rest using AES-256-GCM.
/// The encryption key is derived from the application configuration (<c>Encryption:Key</c>).
/// This key must be configured explicitly; the engine will not start without it.
///
/// Encrypted values are stored as Base64 with an <c>enc:</c> prefix so plain-text
/// values can be distinguished from encrypted ones during migration.
/// </summary>
public static class FieldEncryption
{
    private const string EncryptedPrefix = "enc:";
    private const string EncryptedV2Prefix = "enc2:";
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize   = 16; // AES-GCM standard
    private const int SaltSize  = 16; // Random salt per encryption (v2)
    private const string PasswordChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%&*";

    /// <summary>Returns true if the value is already encrypted (has the <c>enc:</c> or <c>enc2:</c> prefix).</summary>
    public static bool IsEncrypted(string? value)
        => value is not null && (value.StartsWith(EncryptedPrefix, StringComparison.Ordinal)
                              || value.StartsWith(EncryptedV2Prefix, StringComparison.Ordinal));

    /// <summary>
    /// Encrypts a plain-text value using AES-256-GCM with a random per-message salt (v2).
    /// Returns the encrypted value as a Base64 string with an <c>enc2:</c> prefix.
    /// </summary>
    public static string Encrypt(string plainText, string encryptionKey)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(encryptionKey, salt);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        // Format v2: salt + nonce + tag + cipher -> Base64
        var combined = new byte[SaltSize + NonceSize + TagSize + cipher.Length];
        salt.CopyTo(combined, 0);
        nonce.CopyTo(combined, SaltSize);
        tag.CopyTo(combined, SaltSize + NonceSize);
        cipher.CopyTo(combined, SaltSize + NonceSize + TagSize);

        return EncryptedV2Prefix + Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts a value that was encrypted by <see cref="Encrypt"/>.
    /// Supports both v1 (<c>enc:</c>, fixed salt) and v2 (<c>enc2:</c>, random salt).
    /// If the value is not encrypted (no prefix), returns it as-is for backwards compatibility.
    /// </summary>
    public static string Decrypt(string encryptedValue, string encryptionKey)
    {
        if (!IsEncrypted(encryptedValue))
            return encryptedValue; // plain-text fallback

        if (encryptedValue.StartsWith(EncryptedV2Prefix, StringComparison.Ordinal))
            return DecryptV2(encryptedValue, encryptionKey);

        return DecryptV1(encryptedValue, encryptionKey);
    }

    /// <summary>Decrypts v1 format (fixed salt).</summary>
    private static string DecryptV1(string encryptedValue, string encryptionKey)
    {
        var combined = Convert.FromBase64String(encryptedValue[EncryptedPrefix.Length..]);
        var key = DeriveKeyV1(encryptionKey);

        var nonce  = combined.AsSpan(0, NonceSize);
        var tag    = combined.AsSpan(NonceSize, TagSize);
        var cipher = combined.AsSpan(NonceSize + TagSize);
        var plain  = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Decrypts v2 format (random salt stored in ciphertext).</summary>
    private static string DecryptV2(string encryptedValue, string encryptionKey)
    {
        var combined = Convert.FromBase64String(encryptedValue[EncryptedV2Prefix.Length..]);

        var salt   = combined.AsSpan(0, SaltSize);
        var nonce  = combined.AsSpan(SaltSize, NonceSize);
        var tag    = combined.AsSpan(SaltSize + NonceSize, TagSize);
        var cipher = combined.AsSpan(SaltSize + NonceSize + TagSize);
        var plain  = new byte[cipher.Length];

        var key = DeriveKey(encryptionKey, salt.ToArray());

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Generates a cryptographically random 16-character password.</summary>
    public static string GenerateRandomPassword(int length = 16)
    {
        var result = new char[length];
        var bytes = RandomNumberGenerator.GetBytes(length);
        for (int i = 0; i < length; i++)
            result[i] = PasswordChars[bytes[i] % PasswordChars.Length];
        return new string(result);
    }

    /// <summary>Derives a 256-bit key from the encryption key string via PBKDF2 with a random salt (v2).</summary>
    private static byte[] DeriveKey(string key, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(key),
            salt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    /// <summary>Derives a 256-bit key using the fixed v1 salt (backwards compatibility).</summary>
    private static byte[] DeriveKeyV1(string key)
    {
        var salt = "lascodia-field-encryption-salt-v1"u8.ToArray();
        return DeriveKey(key, salt);
    }
}
