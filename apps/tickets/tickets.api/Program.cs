using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Outbox;
using AppPlatform.Tickets.Data;
using AppPlatform.Tickets.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connection = builder.Configuration.GetConnectionString("Tickets")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Tickets is required. Tickets connects as ap_tickets_rt, which can " +
        "read core_v1 and nothing else of core's — see docs/database-privileges.md.");

builder.Services.AddDbContext<TicketsDbContext>(options => options
    .UseNpgsql(connection, npgsql => npgsql
        .MigrationsHistoryTable("__ef_migrations_history", TicketsDbContext.Schema))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.AddAppPlatformAuth(SessionCookie.Tenant);
builder.Services.AddAuthorization();
builder.Services.AddScoped<ISessionStore, NpgsqlSessionStore>();

builder.Services.AddScoped<ITicketService, EFTicketService>();
builder.Services.AddScoped<IEmployeeLookup, EFEmployeeLookup>();
builder.Services.AddScoped<IOutbox>(sp => new Outbox(
    sp.GetRequiredService<TicketsDbContext>(), sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();

app.UseAppPlatformAuth();
// After authentication, so there is a caller whose licensed apps can be read; before
// authorization runs the handler, so an unlicensed tenant never reaches one.
app.UseMiddleware<EntitlementMiddleware>();
app.UseAuthorization();
app.MapEndpoints(Assembly.GetExecutingAssembly());

app.Run();
