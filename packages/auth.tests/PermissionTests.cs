using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPlatform.Auth.Tests;

/// <summary>
/// The permission model (docs/permissions.md): what each role grants, how a caller's permissions
/// are computed, and what the middleware answers.
/// </summary>
public class PermissionTests
{
    private static readonly string[] AllApps = ["tickets", "ledger"];

    private static IReadOnlySet<string> For(string coreRole, params string[] appRoles)
        => RoleCatalogue.PermissionsFor(coreRole, AppRoles.Decode(appRoles), AllApps);

    // ---- The catalogue ----

    [Fact]
    public void A_role_grants_only_permissions_of_its_own_app()
    {
        // Otherwise a ledger role could carry a core permission, and delicensing the ledger would
        // remove it too — or worse, a core permission would hide inside an app role nobody audits.
        foreach (var role in RoleCatalogue.All)
            Assert.All(role.Permissions, p => Assert.Equal(role.App, Permissions.AppOf(p)));
    }

    [Fact]
    public void Every_declared_permission_is_granted_by_some_role()
    {
        // A permission no role grants makes its endpoints unreachable for everyone.
        var declared = typeof(Permissions).GetNestedTypes()
            .SelectMany(t => t.GetFields())
            .Select(f => (string)f.GetValue(null)!);

        Assert.Equal(declared.Order(), RoleCatalogue.AllPermissions.Order());
    }

    [Fact]
    public void Role_names_are_unique_within_an_app()
        => Assert.Equal(
            RoleCatalogue.All.Count,
            RoleCatalogue.All.Select(r => (r.App, r.Name)).Distinct().Count());

    // ---- The matrix: the decisions most worth noticing if they change ----

    [Fact]
    public void A_member_cannot_create_employees_D18()
    {
        Assert.Contains(Permissions.Core.EmployeeRead, For("member"));
        Assert.DoesNotContain(Permissions.Core.EmployeeCreate, For("member"));
        Assert.Contains(Permissions.Core.EmployeeCreate, For("manager"));
    }

    [Fact]
    public void Only_an_admin_manages_users()
    {
        Assert.Contains(Permissions.Core.UserManage, For("admin"));
        Assert.DoesNotContain(Permissions.Core.UserManage, For("manager"));
        Assert.DoesNotContain(Permissions.Core.UserRead, For("member"));
    }

    [Fact]
    public void A_core_admin_has_no_app_permissions_without_an_app_role()
    {
        // No implicit grants: the administrator assigns app roles, their own included.
        Assert.All(For("admin"), p => Assert.Equal(RoleCatalogue.CoreApp, Permissions.AppOf(p)));
    }

    [Fact]
    public void A_requester_raises_tickets_but_does_not_work_them()
    {
        var granted = For("member", "tickets:requester");

        Assert.Contains(Permissions.Tickets.TicketCreate, granted);
        Assert.DoesNotContain(Permissions.Tickets.TicketAssign, granted);
        Assert.DoesNotContain(Permissions.Tickets.TicketClose, granted);
    }

    [Fact]
    public void Only_a_controller_closes_periods_and_creates_accounts()
    {
        var accountant = For("member", "ledger:accountant");
        var controller = For("member", "ledger:controller");

        Assert.Contains(Permissions.Ledger.EntryPost, accountant);
        Assert.DoesNotContain(Permissions.Ledger.PeriodClose, accountant);
        Assert.DoesNotContain(Permissions.Ledger.AccountCreate, accountant);
        Assert.Contains(Permissions.Ledger.PeriodClose, controller);
        Assert.Contains(Permissions.Ledger.AccountCreate, controller);
    }

    // ---- Computing a caller's permissions ----

    [Fact]
    public void An_app_role_grants_nothing_while_the_app_is_unlicensed()
    {
        var granted = RoleCatalogue.PermissionsFor(
            "member", AppRoles.Decode(["ledger:controller"]), licensedApps: ["tickets"]);

        Assert.DoesNotContain(granted, p => Permissions.AppOf(p) == "ledger");
    }

    [Fact]
    public void An_unknown_role_grants_nothing()
    {
        // An older build reading a role a newer one wrote: fail closed.
        Assert.Empty(For("superuser"));
        Assert.Equal(For("member"), For("member", "tickets:overlord"));
    }

    [Fact]
    public void A_core_entry_among_app_roles_is_ignored()
    {
        // Only identity.user.role sets the core role; an app_roles entry claiming one is not a way in.
        Assert.Equal(For("member"), For("member", "core:admin"));
    }

    [Fact]
    public void Malformed_app_role_entries_are_skipped_and_the_first_entry_per_app_wins()
    {
        var decoded = AppRoles.Decode(["", null, "tickets", ":agent", "ledger:", "tickets:agent", "tickets:requester"]);

        Assert.Equal(new Dictionary<string, string> { ["tickets"] = "agent" }, decoded);
    }

    [Fact]
    public void The_access_stamp_does_not_depend_on_entry_order()
        => Assert.Equal(
            AppRoles.AccessStamp("member", AppRoles.Decode(["tickets:agent", "ledger:viewer"])),
            AppRoles.AccessStamp("member", AppRoles.Decode(["ledger:viewer", "tickets:agent"])));

    [Fact]
    public void Changing_an_app_role_ends_the_session_as_role_changed()
    {
        var issuedAs = AppRoles.AccessStamp("admin", AppRoles.Decode(["tickets:agent"]));
        var now = Build.Session() with { AppRoles = AppRoles.Decode(["tickets:requester"]) };

        Assert.Equal(AuthProblem.RoleChanged, SessionEvaluator.Evaluate(now, issuedAs, Build.Now).ProblemCode);
    }

    [Fact]
    public void The_caller_carries_the_permissions_of_its_live_roles()
    {
        var session = Build.Session(role: "member") with { AppRoles = AppRoles.Decode(["tickets:agent"]) };

        var caller = SessionEvaluator.Evaluate(session, ticketAccess: null, Build.Now).Caller!;

        Assert.Contains(Permissions.Tickets.TicketClose, caller.Permissions);
        Assert.DoesNotContain(Permissions.Core.EmployeeCreate, caller.Permissions);
    }

    // ---- Declaring and enforcing ----

    [Fact]
    public void Requiring_a_permission_no_role_grants_fails_at_startup()
    {
        var app = WebApplication.CreateBuilder().Build();

        Assert.Throws<ArgumentException>(() => app.MapGet("/x", () => "x").RequirePermission("tickets.ticket.delete"));
    }

    [Theory]
    [InlineData("/close", "member", "tickets:agent", HttpStatusCode.OK, null)]
    [InlineData("/close", "admin", "tickets:requester", HttpStatusCode.Forbidden, "not_permitted")]
    [InlineData("/post", "admin", "ledger:controller", HttpStatusCode.Forbidden, "app_not_licensed")]
    [InlineData("/me", "member", null, HttpStatusCode.OK, null)]
    [InlineData("/public", "member", null, HttpStatusCode.OK, null)]
    [InlineData("/undeclared", "admin", "tickets:agent", HttpStatusCode.Forbidden, "not_permitted")]
    public async Task The_middleware_enforces_what_each_endpoint_declares(
        string path, string role, string? appRole, HttpStatusCode expected, string? problemCode)
    {
        var caller = new Caller
        {
            PrincipalId = Guid.NewGuid(),
            Kind = PrincipalKind.User,
            UserId = Guid.NewGuid(),
            TenantId = 1,
            CompanyId = 1,
            Role = role,
            LicensedApps = ["tickets"],
            Permissions = RoleCatalogue.PermissionsFor(
                role, AppRoles.Decode(appRole is null ? [] : [appRole]), ["tickets"]),
        };

        using var host = await StartHost(caller);
        var response = await host.GetTestClient().GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        if (problemCode is not null)
            Assert.Contains(problemCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_left_to_authorization()
    {
        // "Sign in" is more useful than "not permitted" to someone who has not signed in.
        using var host = await StartHost(caller: null);

        var response = await host.GetTestClient().GetAsync("/close");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class FixedCaller(Caller? caller) : ICallerContext
    {
        public Caller? Caller => caller;
        public Caller Require() => caller!;
    }

    private static Task<IHost> StartHost(Caller? caller) => new HostBuilder()
        .ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICallerContext>(new FixedCaller(caller));
                services.AddRouting();
                services.AddAuthentication("test").AddScheme<
                    Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, NoSignIn>("test", _ => { });
                services.AddAuthorization();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseMiddleware<PermissionMiddleware>();
                // Stands in for "authenticated": with a caller, authorization lets the request
                // through, so the response reflects only the permission decision.
                app.Use(async (context, next) =>
                {
                    if (caller is not null)
                        context.User = new System.Security.Claims.ClaimsPrincipal(
                            new System.Security.Claims.ClaimsIdentity("test"));
                    await next();
                });
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/close", () => "ok").RequireAuthorization()
                        .RequirePermission(Permissions.Tickets.TicketClose);
                    endpoints.MapGet("/post", () => "ok").RequireAuthorization()
                        .RequirePermission(Permissions.Ledger.EntryPost);
                    endpoints.MapGet("/me", () => "ok").RequireAuthorization().AllowAnySignedInUser();
                    endpoints.MapGet("/public", () => "ok").AllowAnonymous();
                    endpoints.MapGet("/undeclared", () => "ok").RequireAuthorization();
                });
            }))
        .StartAsync();

    /// <summary>Authenticates nobody, so an anonymous request reaches authorization unauthenticated.</summary>
    private sealed class NoSignIn(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<
            Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
    }
}
