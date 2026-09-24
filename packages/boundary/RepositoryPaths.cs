namespace AppPlatform.Boundary;

/// <summary>
/// Locates the repository root from a test's output directory, so a source scan does not
/// depend on how the test runner was invoked or how deep the bin path is.
/// </summary>
public static class RepositoryPaths
{
    private const string RootMarker = "app-platform.slnx";

    public static string Root { get; } = FindRoot();

    public static string Project(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);

        // Fail on a path that does not exist rather than letting a scan over a renamed or
        // not-yet-created project report clean.
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"'{relativePath}' does not exist under the repository root '{Root}'.");
        }

        return path;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate '{RootMarker}' above '{AppContext.BaseDirectory}'.");
    }
}
