using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Auth;
using AppPlatform.Platform.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connection = builder.Configuration.GetConnectionString("Platform")
    ?? throw new InvalidOperationException("ConnectionStrings:Platform is required.");

// The platform NEVER receives Encryption:FieldKey. Withholding it protects encrypted columns
// only — names and emails are plaintext — so the real boundary is that ap_platform_rt holds no
// grant on core, identity, or any app schema. See docs/database-privileges.md.
builder.Services.AddDbContext<PlatformDbContext>(options => options
    .UseNpgsql(connection, npgsql => npgsql
        .MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema))
    .UseSnakeCaseNamingConvention());

builder.Services.AddAppPlatformAuth(SessionCookie.Operator);
builder.Services.AddAuthorization();

builder.Services.AddScoped<OperatorContext>();
builder.Services.AddScoped<IOperatorContext>(sp => sp.GetRequiredService<OperatorContext>());
builder.Services.AddScoped<ICookieAuthenticationState>(sp => sp.GetRequiredService<OperatorContext>());
builder.Services.AddScoped<IOperatorSessionStore, EFOperatorSessionStore>();
builder.Services.AddScoped<ITenantAuditWriter, EFTenantAuditWriter>();
builder.Services.AddScoped<ITenantService, EFTenantService>();
builder.Services.AddScoped<IIdempotencyService, EFIdempotencyService>();

var internalKey = builder.Configuration["Internal:ApiKey"]
    ?? throw new InvalidOperationException(
        "Internal:ApiKey is required: it is the shared secret for core's provisioning endpoint.");

builder.Services.AddHttpClient<IProvisioningClient, HttpProvisioningClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Core:BaseUrl"]
            ?? throw new InvalidOperationException("Core:BaseUrl is required.")))
    .AddTypedClient<IProvisioningClient>((http, _) => new HttpProvisioningClient(http, internalKey));

var app = builder.Build();

app.UseAuthentication();
// The operator middleware, not the tenant one: operators have their own tables, cookie and
// lifetime, and no tenant at all.
app.UseMiddleware<OperatorAuthenticationMiddleware>();
// After authentication, so it knows whether this request is cookie-authenticated; before
// authorization, so a forged mutation is refused before any handler sees it.
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();
app.MapEndpoints(Assembly.GetExecutingAssembly());

app.Run();
