using System.Security.Cryptography;
using System.Text;

namespace LascodiaTradingEngine.Application.Common.Security;

/// <summary>
/// Encrypts and decrypts broker API keys at rest using AES-256-GCM.
/// The encryption key is derived from the application's JWT secret (or a dedicated
/// key configured via <c>Encryption:Key</c>).
///
/// Usage:
/// <code>
/// // Encrypt before storing in DB
/// broker.ApiKey = BrokerKeyEncryption.Encrypt(plainApiKey, encryptionKey);
///
/// // Decrypt when reading for API calls
/// string plain = BrokerKeyEncryption.Decrypt(broker.ApiKey, encryptionKey);
/// </code>
///
/// Encrypted values are stored as Base64 with a <c>enc:</c> prefix so plain-text
/// keys can be distinguished from encrypted ones during migration.
/// </summary>
public static class BrokerKeyEncryption
{
    private const string EncryptedPrefix = "enc:";
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize   = 16; // AES-GCM standard

    /// <summary>Returns true if the value is already encrypted (has the <c>enc:</c> prefix).</summary>
    public static bool IsEncrypted(string? value)
        => value is not null && value.StartsWith(EncryptedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts a plain-text value using AES-256-GCM.
    /// Returns the encrypted value as a Base64 string with an <c>enc:</c> prefix.
    /// </summary>
    public static string Encrypt(string plainText, string encryptionKey)
    {
        var key = DeriveKey(encryptionKey);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        // Format: nonce + tag + cipher → Base64
        var combined = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, NonceSize);
        cipher.CopyTo(combined, NonceSize + TagSize);

        return EncryptedPrefix + Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts a value that was encrypted by <see cref="Encrypt"/>.
    /// If the value is not encrypted (no <c>enc:</c> prefix), returns it as-is for
    /// backwards compatibility during migration.
    /// </summary>
    public static string Decrypt(string encryptedValue, string encryptionKey)
    {
        if (!IsEncrypted(encryptedValue))
            return encryptedValue; // plain-text fallback

        var combined = Convert.FromBase64String(encryptedValue[EncryptedPrefix.Length..]);
        var key = DeriveKey(encryptionKey);

        var nonce  = combined.AsSpan(0, NonceSize);
        var tag    = combined.AsSpan(NonceSize, TagSize);
        var cipher = combined.AsSpan(NonceSize + TagSize);
        var plain  = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Derives a 256-bit key from the encryption key string via SHA256.</summary>
    private static byte[] DeriveKey(string key)
        => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
