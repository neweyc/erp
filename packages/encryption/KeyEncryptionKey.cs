using System.Security.Cryptography;

namespace AppPlatform.Encryption;

/// <summary>
/// The key-encryption key (KEK): the top of the envelope. It encrypts ("wraps") data keys and
/// nothing else, and it never touches the database. Whoever holds it can read every data key the
/// service stores; whoever loses it loses every value encrypted under those keys, permanently.
///
/// It comes from configuration only: an environment variable or a Docker secret today, a KMS
/// later. Envelope encryption is what makes that move cheap: only the data keys are re-wrapped,
/// never the data. Where the production KEK is kept and how it is recovered is
/// docs/open-questions.md, and must be settled before it protects anything real.
///
/// There is no default and no fallback. A missing or malformed value stops the service at
/// startup, because a service that quietly generated its own key would encrypt data that nothing
/// else could ever decrypt.
/// </summary>
public sealed class KeyEncryptionKey
{
    public const int SizeInBytes = 32;

    internal byte[] Bytes { get; }

    /// <summary>
    /// A short, non-secret identifier for this key: the first 8 bytes of its SHA-256, in hex. Stored
    /// beside every data key it wraps, so a service started with the WRONG key fails with a message
    /// saying so, instead of a decryption error in the middle of a sign-in.
    /// </summary>
    public string Fingerprint { get; }

    private KeyEncryptionKey(byte[] bytes)
    {
        Bytes = bytes;
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 8));
    }

    /// <summary>
    /// Reads a base64-encoded 32-byte key from configuration. <paramref name="settingName"/> is used
    /// only in error messages, so an operator knows which setting to fix. Generate one with
    /// <c>openssl rand -base64 32</c>.
    /// </summary>
    public static KeyEncryptionKey FromConfiguration(string? base64Value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(base64Value))
        {
            throw new InvalidOperationException(
                $"{settingName} is required: a base64-encoded {SizeInBytes}-byte key " +
                "(openssl rand -base64 32). There is no default, deliberately.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{settingName} is not valid base64.");
        }

        if (bytes.Length != SizeInBytes)
        {
            throw new InvalidOperationException(
                $"{settingName} must decode to {SizeInBytes} bytes; it decodes to {bytes.Length}.");
        }

        return new KeyEncryptionKey(bytes);
    }
}
