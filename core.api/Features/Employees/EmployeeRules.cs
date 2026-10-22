namespace AppPlatform.Core.Features.Employees;

/// <summary>
/// The rules for an employee's name and email, shared by adding and editing so the two can never
/// accept different things.
/// </summary>
public static class EmployeeRules
{
    /// <summary>A person's details, trimmed, with the email lower-cased (or null for none).</summary>
    public record Details(string FirstName, string LastName, string? Email);

    /// <summary>The cleaned details, or the sentence saying which field to fix.</summary>
    public static (Details? Details, string? Problem) Clean(string? firstName, string? lastName, string? email)
    {
        var first = firstName?.Trim();
        var last = lastName?.Trim();
        // Blank means "no email": an employee need not have one, though they cannot be invited without it.
        var address = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
            return (null, "First and last name are required.");

        // Length is checked HERE, not left to the INSERT. A single over-long name reaching the
        // database fails the whole statement with a message naming a column, instead of the one
        // field the person can fix.
        if (first.Length > 100 || last.Length > 100)
            return (null, "Names are limited to 100 characters.");

        if (address is { Length: > 320 })
            return (null, "Email is limited to 320 characters.");

        return (new Details(first, last, address), null);
    }
}
