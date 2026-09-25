using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Outbox;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddAppPlatformAuth(SessionCookie.Tenant);
builder.Services.AddAuthorization();

builder.Services.AddScoped<ISessionStore>(sp =>
    new NpgsqlSessionStore(Npgsql.NpgsqlDataSource.Create(connection)));

builder.Services.AddScoped<IEmployeeService, EFEmployeeService>();
builder.Services.AddScoped<IUserService, EFUserService>();
builder.Services.AddScoped<IOutbox>(sp => new Outbox(
    sp.GetRequiredService<CoreDbContext>(), sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();

// Never migrate on startup. Schema changes are applied by hand, as the migration role, so a
// deploy cannot silently alter a shared database — and so the runtime role can keep having no
// DDL at all.
app.UseAppPlatformAuth();
app.UseAuthorization();
app.MapEndpoints(Assembly.GetExecutingAssembly());

app.Run();
