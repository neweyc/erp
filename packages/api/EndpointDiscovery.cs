using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace AppPlatform.Api;

public static class EndpointDiscovery
{
    /// <summary>
    /// Finds and maps every <see cref="IEndpoint"/> in the assembly.
    ///
    /// Refuses an assembly with none. A service whose endpoints all failed to be discovered
    /// starts cleanly and serves 404 for everything, which reads as a routing or proxy problem
    /// and sends you looking anywhere but here.
    /// </summary>
    public static IReadOnlyList<string> MapEndpoints(this IEndpointRouteBuilder routes, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(assembly);

        var types = Discover(assembly);

        if (types.Count == 0)
        {
            throw new InvalidOperationException(
                $"No IEndpoint implementations found in {assembly.GetName().Name}. An API that " +
                "maps nothing starts healthy and 404s everything, which looks like a proxy fault.");
        }

        foreach (var type in types)
        {
            ((IEndpoint)Activator.CreateInstance(type)!).Map(routes);
        }

        return [.. types.Select(t => t.FullName!).Order()];
    }

    internal static List<Type> Discover(Assembly assembly)
        => [.. assembly.GetTypes()
            .Where(t => typeof(IEndpoint).IsAssignableFrom(t))
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            // A parameterless constructor is required because endpoints are activated before
            // the request scope exists; dependencies arrive per-request via [FromServices].
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)];
}
