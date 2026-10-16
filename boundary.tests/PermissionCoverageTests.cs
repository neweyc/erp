using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Boundary;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// Every authenticated endpoint of every customer service declares a permission, or says
/// explicitly that any signed-in user may call it (docs/permissions.md §4). Checked on the REAL
/// route table, built from each service's assembly exactly as its Program does, so a new feature is
/// covered the day it is written.
///
/// The runtime backstop refuses an undeclared endpoint too, so this is not what keeps it closed.
/// It is what stops it shipping: a refused-for-everyone route is a defect a customer finds.
/// </summary>
public class PermissionCoverageTests
{
    /// <summary>
    /// The operator console has its own principals and model and does not run
    /// <c>UseAppPlatformAuth</c>; tenant permissions do not apply to it.
    /// </summary>
    private const string OperatorService = "platform.api";

    public static TheoryData<string> CustomerServices
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var service in BoundaryRegistry.Services)
                if (service.ProjectDirectory != OperatorService) data.Add(service.ProjectDirectory);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CustomerServices))]
    public void Every_authenticated_endpoint_declares_its_permission(string projectDirectory)
    {
        var endpoints = RouteTable(projectDirectory);

        // Guards against a discovery failure passing this test by finding nothing to check.
        Assert.Contains(endpoints, PermissionMiddleware.RequiresAuthentication);

        var undeclared = endpoints
            .Where(PermissionMiddleware.RequiresAuthentication)
            .Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null
                        && e.Metadata.GetMetadata<AnySignedInUserMetadata>() is null)
            .Select(e => e.DisplayName)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "Add .RequirePermission(Permissions.…) — or, only for a route about the caller themselves, "
            + ".AllowAnySignedInUser() — to: " + string.Join(", ", undeclared));
    }

    [Fact]
    public void An_endpoint_that_declares_nothing_is_found()
    {
        // The check above, shown able to fail.
        var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/forgotten", () => "x").RequireAuthorization();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).Single();

        Assert.True(PermissionMiddleware.RequiresAuthentication(endpoint));
        Assert.Null(endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>());
    }

    private static List<Microsoft.AspNetCore.Http.Endpoint> RouteTable(string projectDirectory)
    {
        var name = projectDirectory.Split('/')[^1].Replace(".api", "");
        var assembly = Assembly.Load($"AppPlatform.{char.ToUpperInvariant(name[0])}{name[1..]}");

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapEndpoints(assembly);

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
    }
}
