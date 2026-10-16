using System.Text.RegularExpressions;
using AppPlatform.Auth;
using AppPlatform.Boundary;
using AppPlatform.Entitlements;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// The permission catalogue has a TypeScript mirror and names apps that packages/entitlements also
/// declares. Neither copy can drift silently: a UI checking a permission the server never grants
/// hides a feature from everyone, and a permission in an app that does not exist can never be used.
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

        Assert.Equal(PermissionCatalogue.Names.Order(), mirrored);
    }

    [Fact]
    public void Every_app_with_permissions_is_a_licensable_app()
    {
        // A permission's prefix is the app whose licence it depends on, so a prefix that names no
        // app would make the permission impossible to use.
        var appsWithPermissions = PermissionCatalogue.All
            .Select(p => p.App)
            .Where(app => app != Permissions.CoreApp)
            .Distinct()
            .Order();

        Assert.Equal(Apps.All.Order(), appsWithPermissions);
    }

    [GeneratedRegex(@"'([a-z]+\.[a-z.]+)'")]
    private static partial Regex PermissionLiteral();
}
