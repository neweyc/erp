using System.Text.Json;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Auth;
using AppPlatform.Core.Services;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using AppPlatform.Tickets.Data;
using AppPlatform.Tickets.Features.Tickets;
using AppPlatform.Tickets.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// M1's success criterion: provision, accept the invitation, sign in, license tickets, create a
/// ticket — against both services' real migrations, through the real handlers.
///
/// Not a browser test. What it proves is that the PIECES connect: the seams between provisioning
/// and invitation, invitation and sign-in, sign-in and entitlement, entitlement and the app's
/// own data. Every bug found in M1 lived in a seam, and every one of them passed the unit tests
/// on both sides of it.
/// </summary>
public class WalkingSkeletonTests : IAsyncLifetime
{
    private Testcontainers.PostgreSql.PostgreSqlContainer _container = null!;
    private string _connection = "";

    public async Task InitializeAsync()
    {
        _container = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("appplatform").Build();
        await _container.StartAsync();
        _connection = _container.GetConnectionString();

        foreach (var script in new[]
        {
            "database/privileges/01-roles-and-schemas.sql",
            "database/platform/migrations-all.sql",
            "database/core/migrations-all.sql",
            "database/tickets/migrations-all.sql",
        })
        {
            await RunFileAsync(Path.Combine(Boundary.RepositoryPaths.Root, script));
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private async Task RunFileAsync(string path)
    {
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(path))
            .Where(l => !l.TrimStart().StartsWith('\\')));

        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private CoreDbContext Core(AmbientTenantProvider tenant)
        => new(new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(_connection).UseSnakeCaseNamingConvention().Options, tenant);

    private TicketsDbContext Tickets(AmbientTenantProvider tenant)
        => new(new DbContextOptionsBuilder<TicketsDbContext>()
            .UseNpgsql(_connection).UseSnakeCaseNamingConvention().Options, tenant);

    private async Task<(int TenantId, string InviteToken, string AdminEmail)> ProvisionAsync(string name)
    {
        var email = $"admin@{name.ToLowerInvariant()}.test";
        var tenant = new AmbientTenantProvider();
        await using var db = Core(tenant);

        var result = await new Core.Features.Internal.ProvisionTenantFeature
            .ProvisionTenantCommandHandler(db, tenant, TimeProvider.System)
            .Handle(new(name, email, Guid.NewGuid().ToString()));

        Assert.True(result.Succeeded, result.Message);

        var row = await db.Tenants.SingleAsync(t => t.Name == name);

        // The token is read out of the OUTBOX MESSAGE, exactly as the email would carry it —
        // the database stores only a hash, so this is the only place the plaintext exists.
        var scoped = new AmbientTenantProvider();
        scoped.UseTenant(row.Id);
        await using var scopedDb = Core(scoped);

        var payload = await scopedDb.Set<OutboxMessage>()
            .Where(m => m.Destination == email)
            .Select(m => m.Payload)
            .SingleAsync();

        var token = JsonDocument.Parse(payload).RootElement.GetProperty("token").GetString()!;

        return (row.Id, token, email);
    }

    private async Task<Guid> AcceptAndSignInAsync(int tenantId, string email, string password)
    {
        var tenant = new AmbientTenantProvider();
        await using var db = Core(tenant);

        var accepted = await new AcceptInviteFeature.AcceptInviteCommandHandler(
            new EFAuthService(db), tenant, TimeProvider.System)
            .Handle(new(await Task.FromResult(TokenFor(tenantId)), password));

        Assert.True(accepted.Succeeded, accepted.Message);

        var signIn = new AmbientTenantProvider();
        signIn.UseTenant(tenantId);
        await using var signInDb = Core(signIn);

        var outcome = await new SignInFeature.SignInCommandHandler(
            new EFAuthService(signInDb), TimeProvider.System)
            .Handle(tenantId, new(email, password));

        Assert.NotNull(outcome.SessionId);
        return outcome.SessionId.Value;
    }

    private readonly Dictionary<int, string> _tokens = [];
    private string TokenFor(int tenantId) => _tokens[tenantId];

    private async Task LicenseTicketsAsync(int tenantId)
    {
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO platform.tenant_app (tenant_id, app, granted_at) VALUES ($1, 'tickets', now())",
            connection);
        command.Parameters.AddWithValue(tenantId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Caller> ResolveCallerAsync(Guid sessionId)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connection);
        var context = await new NpgsqlSessionStore(dataSource).FindAsync(sessionId);

        var result = SessionEvaluator.Evaluate(context, context?.Role, DateTimeOffset.UtcNow);
        Assert.True(result.Succeeded, $"session rejected: {result.ProblemCode}");

        return result.Caller!;
    }

    [Fact]
    public async Task Provision_accept_sign_in_license_create_ticket()
    {
        var (tenantId, token, email) = await ProvisionAsync("Acme");
        _tokens[tenantId] = token;

        var sessionId = await AcceptAndSignInAsync(tenantId, email, "correct horse battery");

        // Before licensing, the session reports no apps — so the entitlement filter refuses.
        var beforeLicence = await ResolveCallerAsync(sessionId);
        Assert.Empty(beforeLicence.LicensedApps);

        await LicenseTicketsAsync(tenantId);

        // Entitlement is read per request, so licensing takes effect without signing in again.
        var caller = await ResolveCallerAsync(sessionId);
        Assert.Equal(["tickets"], caller.LicensedApps);

        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);
        await using var ticketsDb = Tickets(tenant);

        var created = await new CreateTicketFeature.CreateTicketCommandHandler(
            new EFTicketService(ticketsDb), new EFEmployeeLookup(ticketsDb),
            new Outbox.Outbox(ticketsDb, TimeProvider.System), TimeProvider.System)
            .Handle(caller, new("Printer jammed", "It is jammed.", null));

        Assert.True(created.Succeeded, created.Message);
        Assert.Single(await ticketsDb.Tickets.ToListAsync());
    }

    [Fact]
    public async Task A_ticket_cannot_be_assigned_to_another_tenants_employee()
    {
        var (alphaId, alphaToken, alphaEmail) = await ProvisionAsync("Alpha");
        var (betaId, betaToken, _) = await ProvisionAsync("Beta");
        _tokens[alphaId] = alphaToken;
        _tokens[betaId] = betaToken;

        var sessionId = await AcceptAndSignInAsync(alphaId, alphaEmail, "correct horse battery");
        await LicenseTicketsAsync(alphaId);
        var caller = await ResolveCallerAsync(sessionId);

        // Beta's employee, created directly.
        var beta = new AmbientTenantProvider();
        beta.UseTenant(betaId);
        await using var betaDb = Core(beta);
        var betaEmployee = new Employee
        {
            PublicId = Ids.PublicId.New("emp").ToString(),
            CompanyId = (await betaDb.Companies.SingleAsync()).Id,
            FirstName = "Bea", LastName = "Tester",
        };
        betaDb.Employees.Add(betaEmployee);
        await betaDb.SaveChangesAsync();

        var alpha = new AmbientTenantProvider();
        alpha.UseTenant(alphaId);
        await using var ticketsDb = Tickets(alpha);

        var result = await new CreateTicketFeature.CreateTicketCommandHandler(
            new EFTicketService(ticketsDb), new EFEmployeeLookup(ticketsDb),
            new Outbox.Outbox(ticketsDb, TimeProvider.System), TimeProvider.System)
            .Handle(caller, new("Cross-tenant", null, betaEmployee.PublicId));

        // REFERENCE isolation, not row isolation. There is no foreign key here — PostgreSQL
        // cannot key to a view — so the tenant-filtered lookup returning nothing is the only
        // thing standing between this and a ticket pointing at another customer's employee.
        Assert.False(result.Succeeded);
        Assert.Equal(TicketProblems.AssigneeNotFound, result.ProblemCode);
        Assert.Empty(await ticketsDb.Tickets.ToListAsync());
    }

    [Fact]
    public async Task One_tenants_tickets_are_invisible_to_another()
    {
        var (alphaId, alphaToken, alphaEmail) = await ProvisionAsync("Gamma");
        var (betaId, betaToken, _) = await ProvisionAsync("Delta");
        _tokens[alphaId] = alphaToken;
        _tokens[betaId] = betaToken;

        var sessionId = await AcceptAndSignInAsync(alphaId, alphaEmail, "correct horse battery");
        await LicenseTicketsAsync(alphaId);
        var caller = await ResolveCallerAsync(sessionId);

        var alpha = new AmbientTenantProvider();
        alpha.UseTenant(alphaId);
        await using (var db = Tickets(alpha))
        {
            await new CreateTicketFeature.CreateTicketCommandHandler(
                new EFTicketService(db), new EFEmployeeLookup(db),
                new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
                .Handle(caller, new("Alpha only", null, null));
        }

        var beta = new AmbientTenantProvider();
        beta.UseTenant(betaId);
        await using var betaTickets = Tickets(beta);

        Assert.Empty(await new EFTicketService(betaTickets).ListAsync(includeClosed: true));
    }

    [Fact]
    public async Task An_invitation_cannot_be_accepted_twice()
    {
        var (tenantId, token, email) = await ProvisionAsync("Epsilon");
        _tokens[tenantId] = token;

        await AcceptAndSignInAsync(tenantId, email, "correct horse battery");

        var tenant = new AmbientTenantProvider();
        await using var db = Core(tenant);

        var second = await new AcceptInviteFeature.AcceptInviteCommandHandler(
            new EFAuthService(db), tenant, TimeProvider.System)
            .Handle(new(token, "a different password"));

        // A link that still works after use is a permanent way in for anyone who saw the email.
        Assert.False(second.Succeeded);
        Assert.Equal(AuthProblems.InvalidToken, second.ProblemCode);
    }
}
