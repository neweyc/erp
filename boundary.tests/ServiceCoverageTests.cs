using System.Text.RegularExpressions;
using AppPlatform.Boundary;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// The guard that keeps this suite honest. Every other test here checks a rule; this one
/// checks that the rules are still pointed at all the code.
/// </summary>
public partial class ServiceCoverageTests
{
    [GeneratedRegex(@"Path\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectPath();

    /// <summary>A deployable service, as opposed to a library or a test project.</summary>
    private static bool IsService(string projectPath)
        => projectPath.Contains(".api/", StringComparison.Ordinal)
           && !projectPath.Contains(".tests/", StringComparison.Ordinal);

    private static string[] ServiceProjectsInSolution()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "app-platform.slnx"));

        return [.. ProjectPath().Matches(solution)
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Where(IsService)
            .Select(p => p[..p.LastIndexOf('/')])
            .Distinct(StringComparer.Ordinal)
            .Order()];
    }

    [Fact]
    public void Every_service_in_the_solution_has_a_boundary_entry()
    {
        var registered = BoundaryRegistry.Services
            .Select(s => s.ProjectDirectory)
            .ToHashSet(StringComparer.Ordinal);

        var unregistered = ServiceProjectsInSolution().Where(p => !registered.Contains(p)).ToArray();

        Assert.True(unregistered.Length == 0,
            $"These services are in the solution with no boundary entry: " +
            $"{string.Join(", ", unregistered)}. Add one to BoundaryRegistry.Services — " +
            "a service outside this suite is a service with no enforced boundary.");
    }

    [Fact]
    public void Every_boundary_entry_points_at_a_directory_that_exists()
    {
        // A renamed or deleted project would otherwise leave a stale entry that passes
        // every check by covering nothing.
        foreach (var service in BoundaryRegistry.Services)
        {
            RepositoryPaths.Project(service.ProjectDirectory);
        }
    }

    [Fact]
    public void A_service_may_not_claim_a_published_schema_as_its_own()
    {
        // core.api owns `core` and publishes `core_v1`; nothing owns a published schema as
        // writable territory, or the contract would have a back door.
        foreach (var service in BoundaryRegistry.Services)
        {
            Assert.Empty(service.OwnSchemas.Intersect(BoundaryRegistry.PublishedSchemas, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void The_solution_is_parseable_and_the_detector_finds_projects()
    {
        // Guards the guard: if the slnx format changed and the regex matched nothing,
        // every coverage test above would pass by finding no services at all.
        var solution = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "app-platform.slnx"));

        Assert.NotEmpty(ProjectPath().Matches(solution));
    }
}
