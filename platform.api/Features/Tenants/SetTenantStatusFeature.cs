using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Tenants;

/// <summary>
/// Suspend, resume, retire. The console sets the status; every service reads it back through
/// the published session contract and enforces it at sign-in and on every request.
///
/// (That contract is named in docs/auth-and-access.md rather than here: the boundary source
/// scan is deliberately text-only, so it cannot be fooled — and the price of that is that a
/// service's own prose must not quote another schema's identifiers.)
/// </summary>
public static class SetTenantStatusFeature
{
    public record SetTenantStatusCommand(string? Status, string? Reason);

    public class SetTenantStatusCommandHandler(ITenantService tenants, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Guid operatorId, string tenantPublicId, SetTenantStatusCommand cmd,
            CancellationToken ct = default)
        {
            if (!PublicId.TryParse(tenantPublicId, "ten", out _))
                return CommandResult.NotFound(PlatformProblems.NotFound, "That is not a tenant id.");

            // Matched BY NAME against the documented values, rather than Enum.TryParse.
            // TryParse accepts numeric strings: "999" yields an undefined enum value the
            // handler would then persist as a lifecycle state nothing can interpret, and "0"
            // silently means Active — an undocumented alias that changes meaning the day
            // someone reorders the enum.
            var match = Enum.GetNames<TenantStatus>().FirstOrDefault(
                n => n.Equals(cmd.Status?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is null || !Enum.TryParse<TenantStatus>(match, out var status))
            {
                return CommandResult.Invalid(
                    PlatformProblems.ValidationFailed,
                    $"Status must be one of: {string.Join(", ", Enum.GetNames<TenantStatus>()).ToLowerInvariant()}.");
            }

            var tenant = await tenants.FindByPublicIdAsync(tenantPublicId, ct);
            if (tenant is null)
                return CommandResult.NotFound(PlatformProblems.NotFound, "No such tenant.");

            // Retired is terminal. Reviving one would resurrect an identity whose sessions were
            // revoked and whose data may already have been purged on that promise.
            if (tenant.Status == TenantStatus.Retired && status != TenantStatus.Retired)
            {
                return CommandResult.Invalid(
                    PlatformProblems.TenantRetired,
                    "A retired tenant cannot be reactivated. Provision a new tenant instead.");
            }

            if (tenant.Status == status)
            {
                // Not an error, but not silent either: an operator pressing Suspend on an
                // already-suspended tenant should be told nothing happened, rather than
                // shown success and a fresh audit entry implying it did.
                return CommandResult.Conflict(
                    PlatformProblems.AlreadyInThatState,
                    $"This tenant is already {status.ToString().ToLowerInvariant()}.");
            }

            var previous = tenant.Status;
            tenant.Status = status;
            tenant.StatusChangedAt = clock.GetUtcNow();

            // Staged, so the change and its trail commit together. A status change that
            // committed without its audit row would be an unattributable act by an operator
            // over someone else's business.
            tenants.Audit(new PlatformAuditLog
            {
                PlatformUserId = operatorId,
                Action = $"tenant.{status.ToString().ToLowerInvariant()}",
                TenantPublicId = tenant.PublicId,
                Detail = $"{previous} -> {status}. {cmd.Reason}".Trim(),
                CreatedAt = clock.GetUtcNow(),
            });

            await tenants.SaveAsync(ct);

            return CommandResult.Ok(new { tenantId = tenant.PublicId, status = status.ToString() });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/platform/v1/tenants/{tenantId}/status",
            async (
                string tenantId,
                SetTenantStatusCommand cmd,
                [FromServices] IOperatorContext operatorContext,
                [FromServices] ITenantService tenants,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new SetTenantStatusCommandHandler(tenants, clock);
                // The real operator, so the audit entry names who did it. Guid.Empty here would
                // make every operator action attributable to nobody.
                return (await handler.Handle(operatorContext.Require().PlatformUserId, tenantId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
