using System.Reflection;
using AppPlatform.Boundary;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// Drives the rules FROM the registry, so a service's declared boundary is the thing actually
/// enforced. Without this the registry is documentation: entries could declare banned strings
/// and forbidden references that nothing ever checked.
/// </summary>
public class RegisteredServiceTests
{
    public static TheoryData<string> ServiceNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var service in BoundaryRegistry.Services) data.Add(service.ProjectDirectory);
            return data;
        }
    }

    private static ServiceBoundary Boundary(string name)
        => BoundaryRegistry.Services.Single(s => s.ProjectDirectory == name);

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public void Source_contains_none_of_its_banned_strings(string name)
    {
        var service = Boundary(name);
        if (service.BannedSourceStrings.Length == 0) return;

        var hits = SourceScan.FindBannedStrings(
            RepositoryPaths.Project(service.ProjectDirectory), service.BannedSourceStrings);

        Assert.Empty(hits);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public void Assembly_references_none_of_its_forbidden_assemblies(string name)
    {
        var service = Boundary(name);
        if (service.ForbiddenAssemblyPrefixes.Length == 0) return;

        // Loaded by convention from the project directory name, which is also what keeps the
        // registry entry and the real assembly from drifting apart.
        var assembly = Assembly.Load($"AppPlatform.{Title(service.ProjectDirectory)}");

        Assert.Empty(AssemblyBoundary.FindForbiddenReferences(assembly, service.ForbiddenAssemblyPrefixes));
    }

    [Fact]
    public void At_least_one_service_declares_banned_strings()
    {
        // Guards the guard: every check above returns early on an empty list, so a registry
        // that declared nothing would make the whole suite pass by testing nothing.
        Assert.Contains(BoundaryRegistry.Services, s => s.BannedSourceStrings.Length > 0);
    }

    private static string Title(string projectDirectory)
        => projectDirectory.Split('/')[^1].Replace(".api", "") is var name
            ? char.ToUpperInvariant(name[0]) + name[1..]
            : projectDirectory;
}
