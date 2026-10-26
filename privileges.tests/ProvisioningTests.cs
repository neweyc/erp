using AppPlatform.Audit;
using AppPlatform.Auth;
using AppPlatform.Boundary;
using AppPlatform.Core.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static AppPlatform.Core.Features.Internal.ProvisionTenantFeature;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Provisioning against BOTH services' real migrations.
///
/// This is the test whose absence let two bugs through at once: core's tenant twin omitted a
/// not-null column, and the idempotency key was accepted but never used. Every existing test
/// mocked the provisioning client, so the seam between the two services — which is where both
/// bugs lived — was never executed.
/// </summary>
public class ProvisioningTests : IAsyncLifetime
{
    private Testcontainers.PostgreSql.PostgreSqlContainer _container = null!;
    private string _connection = "";

    public async Task InitializeAsync()
    {
        _container = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("appplatform").Build();
        await _container.StartAsync();
        _connection = _container.GetConnectionString();

        var privileges = RepositoryPaths.Project("database/privileges");
        await RunAsync(Path.Combine(privileges, "00-roles.sql"));
        await RunAsync(Path.Combine(privileges, "01-schemas.sql"));

        // Both services' generated scripts, exactly as the runbook applies them.
        await RunAsync(Path.Combine(RepositoryPaths.Project("database/platform"), "migrations-all.sql"));
        await RunAsync(Path.Combine(RepositoryPaths.Project("database/core"), "migrations-all.sql"));
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private async Task RunAsync(string path)
    {
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(path))
            .Where(l => !l.TrimStart().StartsWith('\\')));

        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The actor is returned so the handler declares "provisioning" on the SAME scope
    /// the context reads from, as it does in the running service.</summary>
    private (CoreDbContext Db, AmbientTenantProvider Tenant, AmbientAuditActor Actor) Open()
    {
        var tenant = new AmbientTenantProvider();
        var actor = new AmbientAuditActor();
        return (new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(_connection).UseSnakeCaseNamingConvention().Options,
            tenant, actor, TimeProvider.System), tenant, actor);
    }

    /// <summary>A fresh address unless one is given, so tests do not collide on addresses by accident.</summary>
    private async Task<Api.CommandResult> ProvisionAsync(string name, string email, string key, string? subdomain = null)
    {
        var (db, tenant, actor) = Open();
        await using var _ = db;

        return await new ProvisionTenantCommandHandler(db, tenant, actor, new Core.Services.EFTenantResolver(db, new Uri("https://app.example.com")), TimeProvider.System)
            .Handle(new(name, email, key, subdomain ?? FreshAddress()));
    }

    private static string FreshAddress() => "t" + Guid.NewGuid().ToString("N")[..12];

    private async Task<int?> ResolveAsync(string host)
    {
        var (db, _, _) = Open();
        await using var _db = db;
        return await new Core.Services.EFTenantResolver(db, new Uri("https://app.example.com")).ResolveAsync(host);
    }

    [Fact]
    public async Task Provisioning_creates_a_tenant_a_company_an_admin_and_an_invite()
    {
        var result = await ProvisionAsync("Acme", "admin@acme.test", Guid.NewGuid().ToString());

        Assert.True(result.Succeeded, $"provisioning failed: {result.Message}");

        var (db, tenant, actor) = Open();
        await using var _ = db;

        var row = await db.Tenants.SingleAsync(t => t.Name == "Acme");
        tenant.UseTenant(row.Id);

        // All four rows, or none. The company and admin are what make a tenant usable; a tenant
        // without them is an account nobody can sign into.
        Assert.Single(await db.Companies.ToListAsync());
        var admin = Assert.Single(await db.Users.ToListAsync());
        Assert.Equal([Auth.BuiltInRoles.CoreAdmin], admin.Roles);
        Assert.Equal(UserStatus.Invited, admin.Status);
        Assert.Single(await db.Set<Outbox.OutboxMessage>().ToListAsync());
    }

    [Fact]
    public async Task The_same_key_replayed_does_not_create_a_second_tenant()
    {
        var key = Guid.NewGuid().ToString();

        await ProvisionAsync("Repeat Ltd", "a@repeat.test", key);
        var second = await ProvisionAsync("Repeat Ltd", "a@repeat.test", key);

        Assert.True(second.Succeeded);

        var (db, _, _) = Open();
        await using var __ = db;
        Assert.Single(await db.Tenants.Where(t => t.ProvisioningKey == key).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_calls_with_one_key_create_exactly_one_tenant()
    {
        var key = Guid.NewGuid().ToString();

        // The case a check-then-insert cannot survive: both callers read nothing and both
        // proceed. Only the unique index decides, and the loser must return the winner's tenant
        // rather than its own — or the same logical operation yields two different ids.
        var results = await Task.WhenAll(
            ProvisionAsync("Race Ltd", "a@race.test", key),
            ProvisionAsync("Race Ltd", "a@race.test", key));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Message));

        var (db, _, _) = Open();
        await using var _unused = db;
        Assert.Single(await db.Tenants.Where(t => t.ProvisioningKey == key).ToListAsync());
    }

    [Fact]
    public async Task A_session_created_against_the_real_schema_resolves_to_an_active_tenant()
    {
        // The test whose absence hid a total auth failure. EF's enum conversion writes "Active";
        // the store matched "active" only, so every valid session fell through to Retired and
        // was rejected — and the other integration suite passed because its hand-written fixture
        // seeded lowercase, accommodating the bug rather than mirroring the migrations.
        await ProvisionAsync("Session Ltd", "a@session.test", Guid.NewGuid().ToString());

        var (db, tenant, actor) = Open();
        await using var _ = db;

        var row = await db.Tenants.SingleAsync(t => t.Name == "Session Ltd");
        tenant.UseTenant(row.Id);

        var user = await db.Users.SingleAsync();

        // Stands in for the person accepting, whom audit requires be named.
        actor.UseActor(AuditActor.User(user.Id));

        // Accepting the invitation is what makes an account usable. A provisioned admin is
        // Invited, and the session function reports user_active false for them — correctly: an
        // invitation that already worked as a login would make the invite meaningless.
        user.Status = UserStatus.Active;

        var session = new Session
        {
            UserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            AbsoluteExpiry = DateTimeOffset.UtcNow.AddDays(7),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        await using var dataSource = NpgsqlDataSource.Create(_connection);
        var context = await new NpgsqlSessionStore(dataSource).FindAsync(session.Id);

        Assert.NotNull(context);
        Assert.Equal(Auth.TenantStatus.Active, context.TenantStatus);
        Assert.Equal(row.Id, context.TenantId);
        Assert.Equal([Auth.BuiltInRoles.CoreAdmin], context.Roles);
        Assert.True(context.UserActive);

        // And it must evaluate to a caller, not merely parse.
        var result = SessionEvaluator.Evaluate(context, PrivilegeFixture.AdminAccess, DateTimeOffset.UtcNow);
        Assert.True(result.Succeeded, $"session rejected: {result.ProblemCode}");
    }

    [Fact]
    public async Task A_suspended_tenant_still_authenticates_against_the_real_schema()
    {
        await ProvisionAsync("Suspended Ltd", "a@susp.test", Guid.NewGuid().ToString());

        var (db, tenant, actor) = Open();
        await using var _ = db;

        var row = await db.Tenants.SingleAsync(t => t.Name == "Suspended Ltd");
        tenant.UseTenant(row.Id);

        // Written as the operator would: the enum name, which is what the platform service
        // persists.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE platform.tenant SET status = 'Suspended' WHERE id = {row.Id}");

        var user = await db.Users.SingleAsync();

        // Stands in for the person accepting, whom audit requires be named.
        actor.UseActor(AuditActor.User(user.Id));

        // Accepting the invitation is what makes an account usable. A provisioned admin is
        // Invited, and the session function reports user_active false for them — correctly: an
        // invitation that already worked as a login would make the invite meaningless.
        user.Status = UserStatus.Active;

        db.Sessions.Add(new Session
        {
            UserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            AbsoluteExpiry = DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();

        var sessionId = (await db.Sessions.SingleAsync()).Id;

        await using var dataSource = NpgsqlDataSource.Create(_connection);
        var context = await new NpgsqlSessionStore(dataSource).FindAsync(sessionId);

        // Suspended must parse as Suspended, not fall through to Retired — the difference is
        // whether the customer can still reach their own data export.
        Assert.Equal(Auth.TenantStatus.Suspended, context!.TenantStatus);

        var result = SessionEvaluator.Evaluate(context, PrivilegeFixture.AdminAccess, DateTimeOffset.UtcNow);
        Assert.True(result.Succeeded);
        Assert.True(result.Caller!.TenantSuspended);
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_refused()
    {
        var result = await ProvisionAsync("No Key Ltd", "a@nokey.test", "  ");

        Assert.False(result.Succeeded);
        Assert.Contains("idempotency key", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_company_and_cannot_see_anothers()
    {
        await ProvisionAsync("Alpha", "a@alpha.test", Guid.NewGuid().ToString());
        await ProvisionAsync("Beta", "b@beta.test", Guid.NewGuid().ToString());

        var (db, tenant, actor) = Open();
        await using var _ = db;

        var alpha = await db.Tenants.SingleAsync(t => t.Name == "Alpha");
        tenant.UseTenant(alpha.Id);

        // The tenant filter, over real rows written by a real transaction.
        var companies = await db.Companies.ToListAsync();
        Assert.Single(companies);
        Assert.Equal("Alpha", companies[0].Name);
    }

    [Fact]
    public async Task A_provisioned_tenant_signs_in_at_its_address_and_nowhere_else()
    {
        var result = await ProvisionAsync("Address Ltd", "a@address.test", Guid.NewGuid().ToString(), "address-ltd");
        Assert.True(result.Succeeded, result.Message);

        var (db, _, _) = Open();
        await using var _db = db;
        var tenantId = (await db.Tenants.SingleAsync(t => t.Name == "Address Ltd")).Id;

        Assert.Equal(tenantId, await ResolveAsync("address-ltd.app.example.com"));
        Assert.Null(await ResolveAsync("app.example.com"));
        Assert.Null(await ResolveAsync("nobody.app.example.com"));
        Assert.Null(await ResolveAsync("address-ltd.elsewhere.com"));
    }

    [Fact]
    public async Task An_invitation_accepted_after_a_rename_answers_with_the_new_address()
    {
        var result = await ProvisionAsync("Renamed Ltd", "a@renamed.test", Guid.NewGuid().ToString(), "renamed-old");
        Assert.True(result.Succeeded, result.Message);

        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        string token;
        await using (var read = new NpgsqlCommand(
            "SELECT payload FROM core.outbox_message WHERE destination = 'a@renamed.test'", connection))
        {
            var payload = System.Text.Json.JsonDocument.Parse((string)(await read.ExecuteScalarAsync())!).RootElement;
            token = payload.GetProperty("token").GetString()!;
        }

        // What the platform's rename does, after the email went out with the old address in it.
        await using (var rename = new NpgsqlCommand(
            "UPDATE platform.tenant_subdomain SET replaced_at = now() WHERE subdomain = 'renamed-old'; " +
            "INSERT INTO platform.tenant_subdomain (subdomain, tenant_id, assigned_at) " +
            "SELECT 'renamed-new', tenant_id, now() FROM platform.tenant_subdomain WHERE subdomain = 'renamed-old';",
            connection))
        {
            await rename.ExecuteNonQueryAsync();
        }

        var (db, tenant, actor) = Open();
        await using var _db = db;
        var accepted = await new Core.Features.Auth.AcceptInviteFeature.AcceptInviteCommandHandler(
                new Core.Services.EFAuthService(db), new Core.Services.EFUserService(db, tenant), tenant, actor,
                new Core.Services.EFTenantResolver(db, new Uri("https://app.example.com")), TimeProvider.System)
            .Handle(new(token, "correct horse battery"));

        // The old link still works, since acceptance finds the tenant from the token, and the answer
        // names the address they sign in at now.
        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.Contains("https://renamed-new.app.example.com",
            System.Text.Json.JsonSerializer.Serialize(accepted.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_administrators_invitation_links_to_the_tenants_address()
    {
        var result = await ProvisionAsync("Linked Ltd", "a@linked.test", Guid.NewGuid().ToString(), "linked-ltd");
        Assert.True(result.Succeeded, result.Message);

        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload FROM core.outbox_message WHERE destination = 'a@linked.test'", connection);
        var payload = System.Text.Json.JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement;

        // Read back from the address row written in provisioning's own transaction.
        Assert.StartsWith("https://linked-ltd.app.example.com/accept-invite?token=", payload.GetProperty("acceptUrl").GetString());
    }

    [Fact]
    public async Task A_former_address_no_longer_signs_anyone_in()
    {
        await ProvisionAsync("Moved Ltd", "a@moved.test", Guid.NewGuid().ToString(), "moved-old");

        // What the platform's rename leaves behind: the old row, replaced.
        await using (var connection = new NpgsqlConnection(_connection))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE platform.tenant_subdomain SET replaced_at = now() WHERE subdomain = 'moved-old'", connection);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await ResolveAsync("moved-old.app.example.com"));
    }

    [Fact]
    public async Task An_address_already_held_is_refused_and_no_second_tenant_is_left()
    {
        await ProvisionAsync("First Ltd", "a@first.test", Guid.NewGuid().ToString(), "shared-name");

        var second = await ProvisionAsync("Second Ltd", "a@second.test", Guid.NewGuid().ToString(), "shared-name");

        Assert.Equal(Api.CoreProblems.SubdomainTaken, second.ProblemCode);
        var (db, _, _) = Open();
        await using var _db = db;
        Assert.False(await db.Tenants.AnyAsync(t => t.Name == "Second Ltd"));
    }

    [Fact]
    public async Task Concurrent_calls_for_one_address_create_exactly_one_tenant()
    {
        // Different keys, so the idempotency index does not decide; both pass the fast check and
        // only the address's primary key stops the second. The loser must leave nothing behind.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            ProvisionAsync($"Contested {i} Ltd", $"a{i}@contested.test", Guid.NewGuid().ToString(), "contested")));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(Api.CoreProblems.SubdomainTaken, r.ProblemCode));

        var (db, _, _) = Open();
        await using var _db = db;
        Assert.Single(await db.Tenants.Where(t => t.Name.StartsWith("Contested ")).ToListAsync());
        Assert.Single(await db.TenantSubdomains.Where(s => s.Subdomain == "contested").ToListAsync());
    }

    [Fact]
    public async Task A_retry_of_a_creation_that_succeeded_gets_its_tenant_back_not_address_taken()
    {
        var key = Guid.NewGuid().ToString();
        await ProvisionAsync("Retried Ltd", "a@retried.test", key, "retried");

        var again = await ProvisionAsync("Retried Ltd", "a@retried.test", key, "retried");

        Assert.True(again.Succeeded, again.Message);
    }

    [Fact]
    public async Task An_unusable_address_is_refused()
    {
        var result = await ProvisionAsync("Bad Address Ltd", "a@bad.test", Guid.NewGuid().ToString(), "www");

        Assert.Equal(Api.CoreProblems.ValidationFailed, result.ProblemCode);
    }
}
