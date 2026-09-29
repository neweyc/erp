using System.Security.Cryptography;
using System.Text;

namespace AppPlatform.Auth;

/// <summary>
/// Time-based one-time passwords (RFC 6238): the six-digit codes an authenticator app shows.
/// HMAC-SHA1, 30-second steps, six digits — the parameters every authenticator app supports.
///
/// Two rules here are the security, not the arithmetic:
///
/// - A code is accepted for its own 30-second step and one step either side, to absorb clock
///   drift between the phone and the server. Wider windows make guessing easier.
/// - A code is accepted ONCE (RFC 6238 §5.2). A code seen over someone's shoulder, or captured in
///   transit, must not sign a second person in during the minute it stays valid. Verify refuses any
///   step at or before the last one used; the caller must then RECORD the step it returns, and do
///   so atomically, or two simultaneous sign-ins with one code can both pass.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>160 bits, the size RFC 4226 recommends for an HMAC-SHA1 secret.</summary>
    private const int SecretBytes = 20;

    /// <summary>A new secret, base32-encoded as authenticator apps expect it.</summary>
    public static string GenerateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    public static string CodeAt(string base32Secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        var hash = HMACSHA1.HashData(Base32.Decode(base32Secret), counter);

        // RFC 4226 §5.3 dynamic truncation.
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step <paramref name="code"/> matches, or null if it matches none that may still be used.
    /// Only steps after <paramref name="lastUsedStep"/> are considered. The caller must record the
    /// returned step before treating the code as accepted.
    /// </summary>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var entered = code?.Replace(" ", "", StringComparison.Ordinal);
        if (entered is not { Length: Digits } || !entered.All(char.IsAsciiDigit)) return null;

        var current = StepAt(now);

        for (var step = current - 1; step <= current + 1; step++)
        {
            if (lastUsedStep is { } used && step <= used) continue;

            var expected = Encoding.ASCII.GetBytes(CodeAt(base32Secret, step));
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(entered)))
                return step;
        }

        return null;
    }

    /// <summary>The otpauth:// link an authenticator app scans as a QR code, or accepts pasted.</summary>
    public static string ProvisioningUri(string issuer, string account, string base32Secret)
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
           $"?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={(int)Step.TotalSeconds}";

    /// <summary>RFC 4648 base32, upper case, unpadded: the alphabet authenticator apps use.</summary>
    internal static class Base32
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Encode(byte[] bytes)
        {
            var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;

            foreach (var b in bytes)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }

            if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return output.ToString();
        }

        public static byte[] Decode(string text)
        {
            var output = new List<byte>(text.Length * 5 / 8);
            int buffer = 0, bits = 0;

            foreach (var c in text.TrimEnd('=').ToUpperInvariant())
            {
                var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
                if (value < 0) throw new FormatException("Not a base32 secret.");

                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                    bits -= 8;
                }
            }

            return [.. output];
        }
    }
}
