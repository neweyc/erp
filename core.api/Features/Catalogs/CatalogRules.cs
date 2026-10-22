namespace AppPlatform.Core.Features.Catalogs;

/// <summary>
/// The name rule shared by departments, job titles and locations, so the three lists can never
/// accept different things.
/// </summary>
public static class CatalogRules
{
    /// <summary>The name trimmed, or the sentence saying what is wrong with it.</summary>
    public static (string? Name, string? Problem) CleanName(string? name, int maxLength)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return (null, "A name is required.");

        // Checked here, not left to the INSERT, so the person sees which field to fix.
        if (trimmed.Length > maxLength)
            return (null, $"Names are limited to {maxLength} characters.");

        return (trimmed, null);
    }

    /// <summary>Whether two names are the same name. Case is ignored: "Sales" and "sales" would be indistinguishable in a picker.</summary>
    public static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
