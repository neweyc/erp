using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPlatform.Auth.Tests;

/// <summary>
/// The permission model (docs/permissions.md): what each built-in role grants, how a caller's
/// permissions are computed from several roles (built-in and custom), and what the middleware answers.
/// </summary>
public class PermissionTests
{
    private static readonly string[] AllApps = ["tickets", "ledger"];

    /// <summary>What these built-in roles grant with every app licensed.</summary>
    private static IReadOnlySet<string> For(params string[] roleKeys)
        => Access.Licensed(Access.Granted(roleKeys, []), AllApps);

    // ---- The catalogue ----

    [Fact]
    public void A_built_in_role_grants_only_permissions_of_its_own_app()
    {
        // Otherwise a ledger role could carry a core permission, and delicensing the ledger would
        // remove it too — or worse, a core permission would hide inside an app role nobody audits.
        foreach (var role in BuiltInRoles.All)
            Assert.All(role.Permissions, p => Assert.Equal(role.App, Permissions.AppOf(p)));
    }

    [Fact]
    public void Every_declared_permission_is_in_the_catalogue_with_a_description()
    {
        var declared = typeof(Permissions).GetNestedTypes()
            .SelectMany(t => t.GetFields())
            .Select(f => (string)f.GetValue(null)!);

        Assert.Equal(declared.Order(), PermissionCatalogue.Names.Order());
        Assert.All(PermissionCatalogue.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void Every_permission_is_granted_by_some_built_in_role()
    {
        // Custom roles could grant one no built-in does, but a tenant that has defined none would
        // then find that endpoint unreachable. Every permission starts out reachable.
        var granted = BuiltInRoles.All.SelectMany(r => r.Permissions).ToHashSet();

        Assert.All(PermissionCatalogue.Names, p => Assert.Contains(p, granted));
    }

    [Fact]
    public void Built_in_keys_are_app_colon_name_and_unique_and_never_look_like_a_custom_role()
    {
        Assert.Equal(BuiltInRoles.All.Count, BuiltInRoles.All.Select(r => r.Key).Distinct().Count());
        Assert.Equal(BuiltInRoles.All.Count, BuiltInRoles.All.Select(r => r.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(BuiltInRoles.All, r =>
        {
            Assert.Matches("^[a-z]+:[a-z]+$", r.Key);
            Assert.False(BuiltInRoles.IsCustomKey(r.Key));
        });
    }

    // ---- The matrix: the decisions most worth noticing if they change ----

    [Fact]
    public void A_member_cannot_create_employees_D18()
    {
        Assert.Contains(Permissions.Core.EmployeeRead, For("core:member"));
        Assert.DoesNotContain(Permissions.Core.EmployeeCreate, For("core:member"));
        Assert.Contains(Permissions.Core.EmployeeCreate, For("core:manager"));
    }

    [Fact]
    public void Only_an_admin_manages_users_and_roles()
    {
        Assert.Contains(Permissions.Core.UserManage, For("core:admin"));
        Assert.Contains(Permissions.Core.RoleManage, For("core:admin"));
        Assert.DoesNotContain(Permissions.Core.UserManage, For("core:manager"));
        Assert.DoesNotContain(Permissions.Core.RoleManage, For("core:manager"));
        Assert.DoesNotContain(Permissions.Core.UserRead, For("core:member"));
    }

    [Fact]
    public void A_core_admin_has_no_app_permissions_without_an_app_role()
    {
        // No implicit grants: the administrator assigns app roles, their own included.
        Assert.All(For("core:admin"), p => Assert.Equal(Permissions.CoreApp, Permissions.AppOf(p)));
    }

    [Fact]
    public void A_requester_raises_tickets_but_does_not_work_them()
    {
        var granted = For("core:member", "tickets:requester");

        Assert.Contains(Permissions.Tickets.TicketCreate, granted);
        Assert.DoesNotContain(Permissions.Tickets.TicketAssign, granted);
        Assert.DoesNotContain(Permissions.Tickets.TicketClose, granted);
    }

    [Fact]
    public void Only_a_controller_closes_periods_and_creates_accounts()
    {
        var accountant = For("core:member", "ledger:accountant");
        var controller = For("core:member", "ledger:controller");

        Assert.Contains(Permissions.Ledger.EntryPost, accountant);
        Assert.DoesNotContain(Permissions.Ledger.PeriodClose, accountant);
        Assert.DoesNotContain(Permissions.Ledger.AccountCreate, accountant);
        Assert.Contains(Permissions.Ledger.PeriodClose, controller);
        Assert.Contains(Permissions.Ledger.AccountCreate, controller);
    }

    // ---- Computing a caller's permissions ----

    [Fact]
    public void Several_roles_grant_the_union_of_their_permissions()
    {
        var granted = For("core:member", "tickets:requester", "ledger:viewer");

        Assert.Equal(
            For("core:member").Concat(For("tickets:requester")).Concat(For("ledger:viewer")).ToHashSet(),
            granted);
    }

    [Fact]
    public void A_custom_roles_permissions_are_granted_alongside_built_in_ones()
    {
        var granted = Access.Granted(["core:member", "rol_x"], [Permissions.Ledger.PeriodClose]);

        Assert.Contains(Permissions.Ledger.PeriodClose, granted);
        Assert.Contains(Permissions.Core.EmployeeRead, granted);
    }

    [Fact]
    public void Permissions_of_an_unlicensed_app_are_not_usable()
    {
        // Built-in or custom alike: delicensing removes access without touching anyone's roles.
        var granted = Access.Granted(["ledger:controller"], [Permissions.Ledger.Read, Permissions.Core.UserRead]);

        var usable = Access.Licensed(granted, licensedApps: ["tickets"]);

        Assert.Equal([Permissions.Core.UserRead], usable);
    }

    [Fact]
    public void Unknown_role_keys_and_unknown_permissions_grant_nothing()
    {
        // An older build reading what a newer one wrote, or a permission since removed: fail closed.
        Assert.Empty(Access.Granted(["core.superuser", "tickets.overlord", "rol_gone", "admin"], []));
        Assert.Empty(Access.Granted([], ["tickets.ticket.delete", "core.everything"]));
    }

    [Fact]
    public void Null_entries_are_skipped_rather_than_thrown_on()
    {
        // text[] NOT NULL still allows NULL elements; this runs on every request.
        Assert.Equal(For("core:member"), Access.Granted([null, "core:member"], [null]));
    }

    [Fact]
    public void The_access_stamp_depends_on_what_is_granted_not_on_order_or_which_role_grants_it()
    {
        Assert.Equal(
            Access.Stamp(Access.Granted(["tickets:agent", "ledger:viewer"], [])),
            Access.Stamp(Access.Granted(["ledger:viewer", "tickets:agent"], [])));

        // Two roles granting the same permissions are, for the shell's purposes, the same access.
        Assert.Equal(
            Access.Stamp(Access.Granted(["ledger:viewer"], [])),
            Access.Stamp(Access.Granted(["rol_reader"], [Permissions.Ledger.Read])));
    }

    [Fact]
    public void Changing_what_the_roles_grant_ends_the_session_as_role_changed()
    {
        var issued = Access.Stamp(Access.Granted(["core:admin", "rol_clerk"], [Permissions.Tickets.TicketRead]));

        // The same roles, but the custom role was edited to grant more.
        var now = Build.Session(roles: ["core:admin", "rol_clerk"]) with
        {
            RolePermissions = [Permissions.Tickets.TicketRead, Permissions.Tickets.TicketClose],
        };

        Assert.Equal(AuthProblem.RoleChanged, SessionEvaluator.Evaluate(now, issued, Build.Now).ProblemCode);
    }

    [Fact]
    public void Licensing_an_app_does_not_end_the_session()
    {
        // The claim is taken BEFORE the licensing filter, so a new licence signs nobody out.
        var issued = Access.Stamp(Access.Granted(["core:admin", "ledger:viewer"], []));
        var now = Build.Session(roles: ["core:admin", "ledger:viewer"]) with { LicensedApps = ["tickets", "ledger"] };

        Assert.True(SessionEvaluator.Evaluate(now, issued, Build.Now).Succeeded);
    }

    [Fact]
    public void The_caller_carries_the_licensed_permissions_of_its_live_roles()
    {
        var session = Build.Session(roles: ["core:member", "tickets:agent", "rol_books"]) with
        {
            RolePermissions = [Permissions.Ledger.Read],
        };

        var caller = SessionEvaluator.Evaluate(session, ticketAccess: null, Build.Now).Caller!;

        Assert.Contains(Permissions.Tickets.TicketClose, caller.Permissions);
        Assert.DoesNotContain(Permissions.Core.EmployeeCreate, caller.Permissions);
        // Build.Session licenses tickets only.
        Assert.DoesNotContain(Permissions.Ledger.Read, caller.Permissions);
        Assert.Equal(["core:member", "tickets:agent", "rol_books"], caller.Roles);
    }

    // ---- Declaring and enforcing ----

    [Fact]
    public void Requiring_a_permission_outside_the_catalogue_fails_at_startup()
    {
        var app = WebApplication.CreateBuilder().Build();

        Assert.Throws<ArgumentException>(() => app.MapGet("/x", () => "x").RequirePermission("tickets.ticket.delete"));
    }

    [Theory]
    [InlineData("/close", "tickets:agent", HttpStatusCode.OK, null)]
    [InlineData("/close", "tickets:requester", HttpStatusCode.Forbidden, "not_permitted")]
    [InlineData("/post", "ledger:controller", HttpStatusCode.Forbidden, "app_not_licensed")]
    [InlineData("/me", null, HttpStatusCode.OK, null)]
    [InlineData("/public", null, HttpStatusCode.OK, null)]
    [InlineData("/undeclared", "tickets:agent", HttpStatusCode.Forbidden, "not_permitted")]
    public async Task The_middleware_enforces_what_each_endpoint_declares(
        string path, string? appRole, HttpStatusCode expected, string? problemCode)
    {
        string[] roles = appRole is null ? ["core:admin"] : ["core:admin", appRole];
        var caller = new Caller
        {
            PrincipalId = Guid.NewGuid(),
            Kind = PrincipalKind.User,
            UserId = Guid.NewGuid(),
            TenantId = 1,
            CompanyId = 1,
            Roles = roles,
            LicensedApps = ["tickets"],
            Permissions = Access.Licensed(Access.Granted(roles, []), ["tickets"]),
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
