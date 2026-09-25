using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Outbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// A command, not a server. Kept in-process so the seed uses the same handlers the application
// does — a separate seeding script is exactly what drifts from the migrations.
if (args.Contains("seed-e2e"))
{
    return await AppPlatform.Core.SeedE2E.RunAsync(
        builder.Configuration.GetConnectionString("Core")
        ?? throw new InvalidOperationException("ConnectionStrings:Core is required."));
}

var connection = builder.Configuration.GetConnectionString("Core")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Core is required. Core connects as ap_core_rt, which holds no DDL " +
        "and no grant on any app schema — see docs/database-privileges.md.");

builder.Services.AddDbContext<CoreDbContext>(options => options
    .UseNpgsql(connection, npgsql => npgsql
        // Each project owns its own history table, in its OWN schema. Left to the default it
        // lands in `public` as "__EFMigrationsHistory" — outside every grant in
        // docs/database-privileges.md, so the boundary simply does not apply to it, and two
        // services sharing the database would then share one history table and corrupt each
        // other's migration chain.
        .MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema))
    // snake_case throughout, per the project convention. Applied as a convention rather than
    // per-property so a new column cannot quietly arrive in PascalCase.
    .UseSnakeCaseNamingConvention());

// The key path is SHARED with every other tenant-facing service: they all read the same auth
// cookie, and a per-process key ring makes that cookie undecryptable one service over.
builder.Services.AddAppPlatformAuth(
    SessionCookie.Tenant, builder.Configuration["DataProtection:KeyPath"]);
builder.Services.AddAuthorization();

// Registered, not newed up per scope: a data source owns a connection pool, and creating one
// per request leaks pools until the process dies. DI also disposes it on shutdown.
// A singleton the container OWNS, so it is disposed on shutdown. A data source holds a
// connection pool; creating one per scope leaks pools until the process dies.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.AddScoped<ISessionStore, NpgsqlSessionStore>();

builder.Services.AddScoped<IAuthService, EFAuthService>();
builder.Services.AddScoped<ITenantResolver, EFTenantResolver>();
builder.Services.AddScoped<IEmployeeService, EFEmployeeService>();
builder.Services.AddScoped<IUserService, EFUserService>();
builder.Services.AddScoped<IOutbox>(sp => new Outbox(
    sp.GetRequiredService<CoreDbContext>(), sp.GetRequiredService<TimeProvider>()));

// Delivery. No real transport exists yet, so capture-to-disk is the only option — and it is
// gated on a non-production environment, because a stray Email:CapturePath in production would
// write customer invitations to disk in plaintext and mark them delivered.
var capturePath = builder.Configuration["Email:CapturePath"];

if (capturePath is { Length: > 0 } && !builder.Environment.IsProduction())
{
    builder.Services.AddSingleton<IOutboxTransport>(new FileEmailTransport(capturePath));
}
else
{
    // Refused at startup rather than at delivery. Without a transport the worker dead-letters
    // every invitation within seconds of staging, and there is no resend feature — recovery
    // would be hand-written SQL, which is exactly what the mission forbids.
    throw new InvalidOperationException(
        "No email transport is configured. Set Email:CapturePath outside Production, or " +
        "register a real transport. Starting without one silently destroys every invitation.");
}

builder.Services.AddOutboxWorker<CoreDbContext>(new OutboxWorkerOptions
{
    Schema = CoreDbContext.Schema,
    // Short, because an invitation the customer is waiting for should not sit for a minute. The
    // claim is indexed and the table is small.
    PollInterval = TimeSpan.FromSeconds(2),
});

var app = builder.Build();

// Never migrate on startup. Schema changes are applied by hand, as the migration role, so a
// deploy cannot silently alter a shared database — and so the runtime role can keep having no
// DDL at all.
app.UseAppPlatformAuth();
app.UseAuthorization();
app.MapEndpoints(Assembly.GetExecutingAssembly());

app.Run();

return 0;
