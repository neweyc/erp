namespace AppPlatform.Ids;

/// <summary>
/// Crockford base32. Chosen over plain base32 or hex for one practical reason: it omits
/// I, L, O, and U, so an id read aloud to support, or copied off a screen, has no
/// ambiguous characters in it — and it defines how to fold the mistakes people still make.
/// </summary>
internal static class Crockford
{
    internal const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>
    /// 25 characters, carrying exactly 125 bits — because 32^25 == 2^125, so every position
    /// is uniformly distributed and the mapping is a bijection.
    ///
    /// 26 characters would hold 130 bits and be the obvious choice for a 128-bit value, but
    /// the encoding is then not onto: the leading character could only ever be 0-7, since
    /// 2^128 / 32^25 == 8. Harmless, and also a visible artefact in every id we would have
    /// had to explain forever. 125 bits of randomness is not meaningfully weaker than 128.
    /// </summary>
    internal const int BodyLength = 25;

    /// <summary>Bits the body can represent: exactly BodyLength * 5.</summary>
    internal const int BodyBits = BodyLength * 5;

    internal static string Encode(UInt128 value)
    {
        var buffer = new char[BodyLength];

        for (var i = BodyLength - 1; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(value % 32)];
            value /= 32;
        }

        return new string(buffer);
    }

    /// <summary>
    /// Folds the substitutions Crockford specifies — O to 0, I and L to 1 — and lowercases.
    /// Someone transcribing an id by hand gets a working lookup instead of a 404 they
    /// cannot explain.
    ///
    /// Returns null for anything outside the alphabet, which is a malformed id rather than
    /// a mistyped one.
    /// </summary>
    internal static string? Normalize(string body)
    {
        if (body.Length != BodyLength) return null;

        var buffer = new char[BodyLength];

        for (var i = 0; i < body.Length; i++)
        {
            var c = char.ToLowerInvariant(body[i]) switch
            {
                'o' => '0',
                'i' or 'l' => '1',
                var other => other,
            };

            if (!Alphabet.Contains(c, StringComparison.Ordinal)) return null;

            buffer[i] = c;
        }

        return new string(buffer);
    }
}
