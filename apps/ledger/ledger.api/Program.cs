using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ledger.Data;
using AppPlatform.Ledger.Services;
using AppPlatform.Outbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connection = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Ledger is required. Ledger connects as ap_ledger_rt, which can " +
        "read core_v1 and nothing else of core's — see docs/database-privileges.md.");

builder.Services.AddDbContext<LedgerDbContext>(options => options
    .UseNpgsql(connection, npgsql => npgsql
        .MigrationsHistoryTable("__ef_migrations_history", LedgerDbContext.Schema))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
// The key path is SHARED with every other tenant-facing service: they all read the same auth
// cookie, and a per-process key ring makes that cookie undecryptable one service over.
builder.Services.AddAppPlatformAuth(
    SessionCookie.Tenant, builder.Configuration["DataProtection:KeyPath"]);
builder.Services.AddAuthorization();
builder.Services.AddScoped<ISessionStore, NpgsqlSessionStore>();

builder.Services.AddScoped<ILedgerService, EFLedgerService>();
builder.Services.AddScoped<IOutbox>(sp => new Outbox(
    sp.GetRequiredService<LedgerDbContext>(), sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();

app.UseAppPlatformAuth();
// After authentication, so there is a caller whose licensed apps can be read; before
// authorization runs the handler, so an unlicensed tenant never reaches one.
app.UseMiddleware<EntitlementMiddleware>();
app.UseAuthorization();
app.MapEndpoints(Assembly.GetExecutingAssembly());

app.Run();
