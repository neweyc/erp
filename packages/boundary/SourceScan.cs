namespace AppPlatform.Boundary;

/// <summary>
/// Scans source text for strings a project must never contain — a configuration key it
/// must not read, a schema it must not name in raw SQL.
///
/// This is the only check here that sees raw SQL at all. The EF model checks are blind to
/// a string handed to FromSqlRaw, which is exactly where a boundary gets crossed by
/// someone in a hurry.
/// </summary>
public static class SourceScan
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git"];

    /// <summary>
    /// Returns "path:line: matched text" for every banned string found under
    /// <paramref name="rootDirectory"/>.
    ///
    /// A missing directory throws rather than returning empty. A scan that silently
    /// passes because it was pointed at nothing is worse than no scan, because the green
    /// result is reported as evidence.
    /// </summary>
    public static IReadOnlyList<string> FindBannedStrings(
        string rootDirectory, IEnumerable<string> banned, params string[] fileExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(banned);

        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Source scan target '{rootDirectory}' does not exist. A scan over nothing " +
                "passes, and a passing scan is read as evidence.");
        }

        var terms = banned.ToArray();
        if (terms.Length == 0) return [];

        var extensions = fileExtensions.Length > 0 ? fileExtensions : [".cs"];
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (IsSkipped(rootDirectory, file)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var term in terms)
                {
                    if (!lines[i].Contains(term, StringComparison.Ordinal)) continue;

                    var relative = Path.GetRelativePath(rootDirectory, file);
                    hits.Add($"{relative}:{i + 1}: {term}");
                }
            }
        }

        return [.. hits.Order()];
    }

    private static bool IsSkipped(string root, string file)
        => Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
