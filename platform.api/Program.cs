using System.Reflection;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Encryption;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Auth;
using AppPlatform.Platform.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connection = builder.Configuration.GetConnectionString("Platform")
    ?? throw new InvalidOperationException("ConnectionStrings:Platform is required.");

// The platform's OWN key-encryption key. It wraps the data keys that protect operator secrets
// (authenticator seeds) and nothing else. The platform never receives the key for CUSTOMER data;
// withholding that protects encrypted columns only (names and emails are plaintext), so the real
// boundary is that ap_platform_rt holds no grant on core, identity, or any app schema. See
// docs/database-privileges.md. Required, with no default: see KeyEncryptionKey.
const string PlatformKekSetting = "Encryption:PlatformKeyEncryptionKey";
var platformKek = KeyEncryptionKey.FromConfiguration(builder.Configuration[PlatformKekSetting], PlatformKekSetting);

// Commands, not a server. Placed before the service checks below so managing operators does not
// require a configured provisioning endpoint.
if (args.Contains("create-platform-user"))
{
    return await AppPlatform.Platform.CreatePlatformUser.RunAsync(connection, platformKek, args);
}

if (args.Contains("reset-platform-user-mfa"))
{
    return await AppPlatform.Platform.ResetPlatformUserMfa.RunAsync(connection, platformKek, args);
}

// Loaded once, before the service accepts requests, and fails startup rather than the first
// sign-in if the configured key is wrong. On the very first start it creates the platform's data key.
await using (var keysDb = PlatformDbContext.ForConnection(connection))
{
    builder.Services.AddSingleton(await KeyRing.LoadOrCreateAsync(keysDb, platformKek, TimeProvider.System));
}

builder.Services.AddDbContext<PlatformDbContext>(options => options
    .UseNpgsql(connection, npgsql => npgsql
        .MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema))
    .UseSnakeCaseNamingConvention());

builder.Services.AddAppPlatformAuth(
    SessionCookie.Operator, builder.Configuration["DataProtection:KeyPath"]);
builder.Services.AddAuthorization();

// Operator sign-in is callable without an account, so it is where guessing
// happens. Limited per client address; see AnonymousRateLimiting for why the proxy list matters.
builder.Services.AddAnonymousRateLimiting(builder.Configuration, builder.Environment);

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

app.UseAnonymousRateLimiting();
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

return 0;
