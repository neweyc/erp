using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Auth;
using AppPlatform.Core.Features.Internal;
using AppPlatform.Core.Services;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AppPlatform.Core;

/// <summary>
/// Seeds a signed-in-able tenant for the browser journey.
///
/// Deliberately runs the REAL provisioning and accept-invite handlers rather than inserting
/// rows. Hand-written seed SQL is how a fixture drifts from the migrations — which in this
/// repo has now produced a missing column, a missing function, and a total auth failure that
/// all passed their tests. If the seed cannot provision, the browser test should not run.
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

        // The invitation token exists only in the outbox message, exactly as it would only
        // exist in the email — so the seed reads it the same way the journey test does.
        var scoped = new AmbientTenantProvider();
        await using var read = new CoreDbContext(options, scoped);

        var row = await read.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == TenantName);
        scoped.UseTenant(row.Id);

        var payload = await read.Set<OutboxMessage>()
            .Where(m => m.Destination == AdminEmail)
            .Select(m => m.Payload)
            .SingleAsync();

        var token = JsonDocument.Parse(payload).RootElement.GetProperty("token").GetString()!;

        var accepting = new AmbientTenantProvider();
        await using var acceptDb = new CoreDbContext(options, accepting);

        var accepted = await new AcceptInviteFeature.AcceptInviteCommandHandler(
            new EFAuthService(acceptDb), accepting, TimeProvider.System)
            .Handle(new(token, AdminPassword));

        if (!accepted.Succeeded)
        {
            Console.Error.WriteLine($"seed-e2e: accept-invite failed: {accepted.Message}");
            return 1;
        }

        // Licensed directly: granting an entitlement is the platform's job, and the browser
        // journey is about the tenant surface rather than the operator console.
        await acceptDb.Database.ExecuteSqlAsync(
            $"INSERT INTO platform.tenant_app (tenant_id, app, granted_at) VALUES ({row.Id}, 'tickets', now()) ON CONFLICT DO NOTHING");

        Console.WriteLine($"seed-e2e: tenant {row.PublicId} ready ({AdminEmail})");
        return 0;
    }
}
