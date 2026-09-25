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
        await RunAsync(Path.Combine(privileges, "01-roles-and-schemas.sql"));

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

    private (CoreDbContext Db, AmbientTenantProvider Tenant) Open()
    {
        var tenant = new AmbientTenantProvider();
        return (new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(_connection).UseSnakeCaseNamingConvention().Options,
            tenant), tenant);
    }

    private async Task<Api.CommandResult> ProvisionAsync(string name, string email, string key)
    {
        var (db, tenant) = Open();
        await using var _ = db;

        return await new ProvisionTenantCommandHandler(db, tenant, TimeProvider.System)
            .Handle(new(name, email, key));
    }

    [Fact]
    public async Task Provisioning_creates_a_tenant_a_company_an_admin_and_an_invite()
    {
        var result = await ProvisionAsync("Acme", "admin@acme.test", Guid.NewGuid().ToString());

        Assert.True(result.Succeeded, $"provisioning failed: {result.Message}");

        var (db, tenant) = Open();
        await using var _ = db;

        var row = await db.Tenants.SingleAsync(t => t.Name == "Acme");
        tenant.UseTenant(row.Id);

        // All four rows, or none. The company and admin are what make a tenant usable; a tenant
        // without them is an account nobody can sign into.
        Assert.Single(await db.Companies.ToListAsync());
        var admin = Assert.Single(await db.Users.ToListAsync());
        Assert.Equal("admin", admin.Role);
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

        var (db, _) = Open();
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

        var (db, _) = Open();
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

        var (db, tenant) = Open();
        await using var _ = db;

        var row = await db.Tenants.SingleAsync(t => t.Name == "Session Ltd");
        tenant.UseTenant(row.Id);

        var user = await db.Users.SingleAsync();

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
        Assert.Equal("admin", context.Role);
        Assert.True(context.UserActive);

        // And it must evaluate to a caller, not merely parse.
        var result = SessionEvaluator.Evaluate(context, "admin", DateTimeOffset.UtcNow);
        Assert.True(result.Succeeded, $"session rejected: {result.ProblemCode}");
    }

    [Fact]
    public async Task A_suspended_tenant_still_authenticates_against_the_real_schema()
    {
        await ProvisionAsync("Suspended Ltd", "a@susp.test", Guid.NewGuid().ToString());

        var (db, tenant) = Open();
        await using var _ = db;

        var row = await db.Tenants.SingleAsync(t => t.Name == "Suspended Ltd");
        tenant.UseTenant(row.Id);

        // Written as the operator would: the enum name, which is what the platform service
        // persists.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE platform.tenant SET status = 'Suspended' WHERE id = {row.Id}");

        var user = await db.Users.SingleAsync();

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

        var result = SessionEvaluator.Evaluate(context, "admin", DateTimeOffset.UtcNow);
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

        var (db, tenant) = Open();
        await using var _ = db;

        var alpha = await db.Tenants.SingleAsync(t => t.Name == "Alpha");
        tenant.UseTenant(alpha.Id);

        // The tenant filter, over real rows written by a real transaction.
        var companies = await db.Companies.ToListAsync();
        Assert.Single(companies);
        Assert.Equal("Alpha", companies[0].Name);
    }
}
