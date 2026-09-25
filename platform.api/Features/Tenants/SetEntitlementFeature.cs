using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Tenants;

/// <summary>
/// Grants or revokes an app. This IS the business model, so it exists from the first commit
/// rather than being designed and deferred the way EMS's module entitlements were.
///
/// Revoking hides functionality; it never deletes data. A tenant that stops paying and later
/// resumes must find their tickets where they left them.
/// </summary>
public static class SetEntitlementFeature
{
    /// <summary>
    /// Core is deliberately absent. It is always on and has no row — an entitlement table that
    /// could express "core revoked" invites someone to try it.
    /// </summary>
    public static readonly string[] LicensableApps = ["tickets"];

    public record SetEntitlementCommand(string? App, bool? Licensed);

    public class SetEntitlementCommandHandler(ITenantService tenants, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Guid operatorId, string tenantPublicId, SetEntitlementCommand cmd,
            CancellationToken ct = default)
        {
            if (!PublicId.TryParse(tenantPublicId, "ten", out _))
                return CommandResult.NotFound(PlatformProblems.NotFound, "That is not a tenant id.");

            var app = cmd.App?.Trim().ToLowerInvariant();
            if (app is null || !LicensableApps.Contains(app))
            {
                return CommandResult.Invalid(
                    PlatformProblems.ValidationFailed,
                    $"App must be one of: {string.Join(", ", LicensableApps)}.");
            }

            if (cmd.Licensed is not { } licensed)
            {
                // Required rather than defaulted: both defaults are wrong in a way the caller
                // cannot see. Defaulting to true grants silently; defaulting to false revokes.
                return CommandResult.Invalid(
                    PlatformProblems.ValidationFailed,
                    "Specify licensed: true or false. There is no safe default for this field.");
            }

            var tenant = await tenants.FindByPublicIdAsync(tenantPublicId, ct);
            if (tenant is null)
                return CommandResult.NotFound(PlatformProblems.NotFound, "No such tenant.");

            var now = clock.GetUtcNow();
            var existing = await tenants.FindGrantAsync(tenant.Id, app, ct);

            if (licensed)
            {
                if (existing is not null)
                {
                    return CommandResult.Conflict(
                        PlatformProblems.AppAlreadyLicensed, $"{app} is already licensed for this tenant.");
                }

                tenants.Add(new TenantApp { TenantId = tenant.Id, App = app, GrantedAt = now });
            }
            else
            {
                if (existing is null)
                    return CommandResult.Conflict(PlatformProblems.AlreadyInThatState, $"{app} is not licensed.");

                // Revoked, not deleted: the grant history is the commercial record of what a
                // customer had and when.
                existing.RevokedAt = now;
            }

            tenants.Audit(new PlatformAuditLog
            {
                PlatformUserId = operatorId,
                Action = licensed ? "entitlement.granted" : "entitlement.revoked",
                TenantPublicId = tenant.PublicId,
                Detail = app,
                CreatedAt = now,
            });

            try
            {
                await tenants.SaveAsync(ct);
            }
            catch (Exception ex) when (licensed && DatabaseConflict.IsUniqueViolation(ex))
            {
                // Two operators granting the same app concurrently both pass the preflight read
                // above — it cannot see an insert that has not happened yet. The partial unique
                // index is what keeps the data correct; without this the loser got an unhandled
                // 500 naming a constraint, which tells the operator nothing they can act on.
                return CommandResult.Conflict(
                    PlatformProblems.AppAlreadyLicensed, $"{app} is already licensed for this tenant.");
            }

            return CommandResult.Ok(new { tenantId = tenant.PublicId, app, licensed });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/platform/v1/tenants/{tenantId}/entitlements",
            async (
                string tenantId,
                SetEntitlementCommand cmd,
                [FromServices] IOperatorContext operatorContext,
                [FromServices] ITenantService tenants,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new SetEntitlementCommandHandler(tenants, clock);
                // The real operator, so the audit entry names who did it. Guid.Empty here would
                // make every operator action attributable to nobody.
                return (await handler.Handle(operatorContext.Require().PlatformUserId, tenantId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
