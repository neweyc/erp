using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace AppPlatform.Ids;

/// <summary>
/// The identifier that leaves the building: in API responses, webhook payloads, CSV
/// exports, and URLs. Stored in its own column, separate from the primary key, which never
/// leaves the database.
///
/// Why not the primary key:
/// - A sequential integer leaks row counts and growth rate across tenants, and cannot be
///   re-keyed once a customer has stored it.
/// - It welds the external contract to a storage decision. Changing the key type later
///   then means changing every integration.
///
/// **This is the one genuinely irreversible decision in the integration design.** Once a
/// customer has written our ids into their system, they are permanent.
///
/// Why RANDOM rather than time-ordered (UUIDv7, ULID): a sortable id publishes creation
/// order and approximate creation time to anyone holding two of them. Insert locality on a
/// secondary unique index is worth less than not disclosing that.
/// </summary>
public readonly record struct PublicId
{
    private PublicId(string prefix, string body)
    {
        Prefix = prefix;
        Body = body;
    }

    public string Prefix { get; }

    /// <summary>The encoded random part, without the prefix.</summary>
    public string Body { get; }

    /// <summary>The canonical text form — what is stored and what callers see.</summary>
    public override string ToString() => $"{Prefix}_{Body}";

    public static PublicId New(string prefix)
    {
        PublicIdPrefix.Validate(prefix);

        // From a cryptographic source. A public id appears in URLs and logs, so it must be
        // unguessable: anything predictable turns "knows an id" into "can enumerate the
        // tenant", with only the tenant filter left in the way.
        var bytes = RandomNumberGenerator.GetBytes(16);
        var value = new UInt128(
            BitConverter.ToUInt64(bytes, 0),
            BitConverter.ToUInt64(bytes, 8));

        // Masked to exactly what the body can represent, so every character is uniformly
        // distributed rather than the leading one being constrained to a subrange.
        var mask = (UInt128.One << Crockford.BodyBits) - 1;

        return new PublicId(prefix, Crockford.Encode(value & mask));
    }

    /// <summary>
    /// Parses an id and requires it to be of the expected kind.
    ///
    /// The prefix is not decoration. Without this check a ticket id is accepted wherever an
    /// employee id was meant: the lookup finds nothing, the caller gets a 404, and the real
    /// mistake — two different kinds of thing being passed through the same parameter — is
    /// invisible. With it, the wrong kind of id fails at the boundary, naming both kinds.
    /// </summary>
    public static bool TryParse(
        string? input, string expectedPrefix, [NotNullWhen(true)] out PublicId? id)
    {
        PublicIdPrefix.Validate(expectedPrefix);
        id = null;

        if (string.IsNullOrEmpty(input)) return false;

        var separator = input.IndexOf('_', StringComparison.Ordinal);
        if (separator <= 0) return false;

        // Compared case-insensitively so a shouted or auto-capitalised id still parses,
        // while the canonical form stays lowercase.
        var prefix = input[..separator];
        if (!prefix.Equals(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var body = Crockford.Normalize(input[(separator + 1)..]);
        if (body is null) return false;

        id = new PublicId(expectedPrefix, body);
        return true;
    }

    /// <summary>
    /// Parses without knowing the kind — for logging and diagnostics, where seeing that an
    /// id is a `tkt_` is the point. Never use this to look something up: it is exactly the
    /// check <see cref="TryParse"/> exists to enforce.
    /// </summary>
    public static bool TryParseAny(string? input, [NotNullWhen(true)] out PublicId? id)
    {
        id = null;

        if (string.IsNullOrEmpty(input)) return false;

        var separator = input.IndexOf('_', StringComparison.Ordinal);
        if (separator <= 0) return false;

        var prefix = input[..separator].ToLowerInvariant();
        if (!PublicIdPrefix.IsValid(prefix)) return false;

        var body = Crockford.Normalize(input[(separator + 1)..]);
        if (body is null) return false;

        id = new PublicId(prefix, body);
        return true;
    }
}
