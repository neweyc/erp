using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Internal;
using AppPlatform.Core.Services;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core;

/// <summary>
/// Provisions the tenant the browser journey starts from.
///
/// Runs the REAL provisioning handler rather than inserting rows: hand-written seed SQL is how a
/// fixture drifts from the migrations, which in this repo has already produced a missing column,
/// a missing function, and a total auth failure that all passed their tests.
///
/// It stops at provisioning. The invitation is delivered by the outbox worker and accepted in
/// the browser, because "the invitation arrives" is part of the journey being proved.
/// </summary>
public static class SeedE2E
{
    public const string TenantName = "E2E Ltd";
    public const string AdminEmail = "admin@e2e.test";
    public const string AdminPassword = "correct horse battery";

    public static async Task<int> RunAsync(string connectionString)
    {
        var tenant = new AmbientTenantProvider();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var db = new CoreDbContext(options, tenant))
        {
            if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Name == TenantName))
            {
                Console.WriteLine("seed-e2e: already seeded");
                return 0;
            }

            var provisioned = await new ProvisionTenantFeature.ProvisionTenantCommandHandler(
                db, tenant, TimeProvider.System)
                .Handle(new(TenantName, AdminEmail, "e2e-seed"));

            if (!provisioned.Succeeded)
            {
                Console.Error.WriteLine($"seed-e2e: provisioning failed: {provisioned.Message}");
                return 1;
            }
        }

        // Deliberately does NOT accept the invitation. Acceptance is part of the journey and
        // happens in the browser, using the token from the DELIVERED message — reading it out of
        // the database here would prove nothing about whether invitations are ever sent.
        await using var read = new CoreDbContext(options, new AmbientTenantProvider());

        // IgnoreQueryFilters because there is no session here to supply a tenant, and the only
        // other statement is raw SQL — neither consults the tenant scope.
        var row = await read.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == TenantName);

        // Licensed directly: granting an entitlement is the platform's job, and the browser
        // journey is about the tenant surface rather than the operator console.
        await read.Database.ExecuteSqlAsync(
            $"INSERT INTO platform.tenant_app (tenant_id, app, granted_at) VALUES ({row.Id}, 'tickets', now()) ON CONFLICT DO NOTHING");

        Console.WriteLine($"seed-e2e: tenant {row.PublicId} provisioned and licensed; " +
            $"invitation for {AdminEmail} awaits delivery");
        return 0;
    }
}
