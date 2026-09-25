namespace AppPlatform.Ids;

/// <summary>
/// Rules for the prefix itself. Kept narrow so an id is unambiguous in a log line and
/// safe in a URL without escaping.
/// </summary>
public static class PublicIdPrefix
{
    public const int MinLength = 2;
    public const int MaxLength = 8;

    public static bool IsValid(string? prefix)
        => prefix is { Length: >= MinLength and <= MaxLength }
           && prefix.All(c => c is >= 'a' and <= 'z');

    public static void Validate(string prefix)
    {
        if (!IsValid(prefix))
        {
            throw new ArgumentException(
                $"'{prefix}' is not a valid public id prefix: {MinLength}-{MaxLength} lowercase " +
                "letters, no digits or punctuation. Digits would be ambiguous against the " +
                "encoded body, and an underscore would break parsing.",
                nameof(prefix));
        }
    }
}
