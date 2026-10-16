using System.Text.RegularExpressions;
using AppPlatform.Auth;
using AppPlatform.Boundary;
using AppPlatform.Entitlements;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// The permission catalogue has a TypeScript mirror and names apps that packages/entitlements also
/// declares. Neither copy can drift silently: a UI checking a permission the server never grants
/// hides a feature from everyone, and a role in an app that does not exist can never be licensed.
/// </summary>
public partial class PermissionCatalogueTests
{
    [Fact]
    public void The_TypeScript_mirror_lists_exactly_the_dotnet_permissions()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryPaths.Project("packages/web-api/src"), "permissions.ts"));

        // Every quoted dotted name in the file. The file holds nothing else of that shape.
        var mirrored = PermissionLiteral().Matches(source).Select(m => m.Groups[1].Value).Order();

        Assert.Equal(RoleCatalogue.AllPermissions.Order(), mirrored);
    }

    [Fact]
    public void Every_app_with_roles_is_a_licensable_app()
    {
        var appsWithRoles = RoleCatalogue.All
            .Select(r => r.App)
            .Where(app => app != RoleCatalogue.CoreApp)
            .Distinct()
            .Order();

        Assert.Equal(Apps.All.Order(), appsWithRoles);
    }

    [GeneratedRegex(@"'([a-z]+\.[a-z.]+)'")]
    private static partial Regex PermissionLiteral();
}
