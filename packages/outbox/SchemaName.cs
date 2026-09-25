using System.Text.RegularExpressions;

namespace AppPlatform.Outbox;

/// <summary>
/// Validates a schema identifier before it is interpolated into SQL.
///
/// One copy, because this is a security rule: a schema cannot be a SQL parameter, so every place
/// that builds a statement from one depends on the same check. Two copies can drift, and the one
/// that drifts is the injection point.
/// </summary>
internal static partial class SchemaName
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Allowed();

    public static bool IsValid(string? schema) => schema is not null && Allowed().IsMatch(schema);

    public static void Validate(string? schema, string parameterName)
    {
        if (!IsValid(schema))
            throw new ArgumentException($"'{schema}' is not a valid schema identifier.", parameterName);
    }
}
