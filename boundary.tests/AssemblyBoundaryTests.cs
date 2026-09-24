using AppPlatform.Boundary;
using AppPlatform.Tenancy;

namespace AppPlatform.BoundaryTests;

public class AssemblyBoundaryTests
{
    [Fact]
    public void A_forbidden_direct_reference_is_reported()
    {
        var problems = AssemblyBoundary.FindForbiddenReferences(
            typeof(AssemblyBoundaryTests).Assembly, ["AppPlatform.Tenancy"]);

        Assert.Contains(problems, p => p.Contains("AppPlatform.Tenancy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_forbidden_transitive_reference_is_reported()
    {
        var assembly = typeof(AssemblyBoundaryTests).Assembly;

        var direct = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToHashSet(StringComparer.Ordinal);

        // Discovered rather than hardcoded: which assemblies arrive transitively shifts
        // with package versions, and a test naming one would start passing vacuously the
        // day that changed.
        var transitiveOnly = AssemblyBoundary.ReferenceClosure(assembly)
            .Where(name => !direct.Contains(name))
            .ToArray();

        Assert.NotEmpty(transitiveOnly);

        // A direct-only check would miss every one of these, which is the case that lets
        // platform -> shared -> core back in.
        Assert.NotEmpty(AssemblyBoundary.FindForbiddenReferences(assembly, [transitiveOnly[0]]));
    }

    [Fact]
    public void An_assembly_with_no_forbidden_reference_is_clean()
        => Assert.Empty(AssemblyBoundary.FindForbiddenReferences(
            typeof(ITenantScoped).Assembly, ["AppPlatform.Core", "AppPlatform.Platform"]));
}
