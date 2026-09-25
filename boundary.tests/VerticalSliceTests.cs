using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Boundary;

namespace AppPlatform.BoundaryTests;

/// <summary>Checks the slice structure. Whether a handler hides its workflow behind
/// unnecessary services remains a required human/agent review, not a naming heuristic.</summary>
public class VerticalSliceTests
{
    [Theory]
    [MemberData(nameof(RegisteredServiceTests.ServiceNames), MemberType = typeof(RegisteredServiceTests))]
    public void Endpoints_belong_to_named_feature_slices_with_local_handlers(string projectDirectory)
    {
        var name = projectDirectory.Split('/')[^1].Replace(".api", "");
        var assembly = Assembly.Load($"AppPlatform.{char.ToUpperInvariant(name[0])}{name[1..]}");
        var endpoints = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IEndpoint).IsAssignableFrom(t)).ToArray();
        Assert.NotEmpty(endpoints);

        foreach (var endpoint in endpoints)
        {
            Assert.True(IsSliceEndpoint(endpoint),
                $"{endpoint.FullName} must be Endpoint nested in a static *Feature class " +
                "under .Features.<Area>, with a nested command/query handler.");

            var feature = endpoint.DeclaringType!;
            const string featureNamespace = ".Features.";
            var namespaceName = feature.Namespace!;
            var area = namespaceName[(namespaceName.IndexOf(featureNamespace, StringComparison.Ordinal)
                + featureNamespace.Length)..];
            var file = Path.Combine(RepositoryPaths.Project(projectDirectory), "Features",
                area.Replace('.', Path.DirectorySeparatorChar), feature.Name + ".cs");
            Assert.True(File.Exists(file), $"Expected slice file: {file}");
        }
    }

    private static bool IsSliceEndpoint(Type endpoint)
    {
        var feature = endpoint.DeclaringType;
        return endpoint.Name == "Endpoint"
            && feature is { IsAbstract: true, IsSealed: true }
            && feature.Name.EndsWith("Feature", StringComparison.Ordinal)
            && feature.Namespace?.Contains(".Features.", StringComparison.Ordinal) == true
            && feature.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Any(t => t.Name.EndsWith("CommandHandler", StringComparison.Ordinal)
                       || t.Name.EndsWith("QueryHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void Structural_check_rejects_an_endpoint_outside_a_slice()
        => Assert.False(IsSliceEndpoint(typeof(UnscopedEndpoint)));

    private sealed class UnscopedEndpoint : IEndpoint
    {
        public void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder routes) { }
    }
}
