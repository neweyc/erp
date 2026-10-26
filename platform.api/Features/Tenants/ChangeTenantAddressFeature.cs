using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Services;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Tenants;

/// <summary>
/// Moves a tenant to a new address: <c>acme</c> becomes <c>acme-fire</c>, so its people now sign
/// in at <c>acme-fire.{base domain}</c>.
///
/// The old address stops signing anyone in at once. People already signed in there stay signed in
/// until their session ends, because their cookie belongs to that host. The old address stays
/// reserved to this tenant, so no other tenant can take it. The tenant may take it back later.
/// </summary>
public static class ChangeTenantAddressFeature
{
    public record ChangeTenantAddressCommand(string? Subdomain);

    public class ChangeTenantAddressCommandHandler(ITenantService tenants, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Guid operatorId, string tenantPublicId, ChangeTenantAddressCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(tenantPublicId, "ten", out _))
                return CommandResult.NotFound(PlatformProblems.NotFound, "That is not a tenant id.");

            if (!TenantAddress.TryNormalize(cmd.Subdomain, out var subdomain, out var problem))
                return CommandResult.Invalid(PlatformProblems.ValidationFailed, problem);

            var tenant = await tenants.FindByPublicIdAsync(tenantPublicId, ct);
            if (tenant is null)
                return CommandResult.NotFound(PlatformProblems.NotFound, "No such tenant.");

            // Nobody signs in to a retired tenant, so a new address would only use up a name.
            if (tenant.Status == TenantStatus.Retired)
                return CommandResult.Invalid(PlatformProblems.TenantRetired, "A retired tenant's address cannot be changed.");

            var current = await tenants.CurrentAddressAsync(tenant.Id, ct);
            if (current?.Subdomain == subdomain)
            {
                return CommandResult.Conflict(
                    PlatformProblems.AlreadyInThatState, $"This tenant already signs in at \"{subdomain}\".");
            }

            // Any holder, current or former. The tenant's own former address is the one exception.
            var holder = await tenants.FindAddressAsync(subdomain, ct);
            if (holder is not null && holder.TenantId != tenant.Id)
                return Taken(subdomain);

            var now = clock.GetUtcNow();
            await using var transaction = await tenants.BeginTransactionAsync(ct);

            // Two saves, in this order. The database allows one current address per tenant, and
            // one save may write the new row before it retires the old one, so the old one goes first.
            if (current is not null)
            {
                current.ReplacedAt = now;
                // The span it was in use, kept here because taking this address back later would
                // overwrite the dates on its own row.
                tenants.Add(new TenantAddressPeriod
                {
                    TenantId = tenant.Id, Subdomain = current.Subdomain, AssignedAt = current.AssignedAt, ReplacedAt = now,
                });
                await tenants.SaveAsync(ct);
            }

            if (holder is not null)
            {
                // Taking back one of its own former addresses.
                holder.ReplacedAt = null;
                holder.AssignedAt = now;
            }
            else
            {
                tenants.Add(new TenantSubdomain { Subdomain = subdomain, TenantId = tenant.Id, AssignedAt = now });
            }

            tenants.Audit(new PlatformAuditLog
            {
                PlatformUserId = operatorId,
                Action = "tenant.address_changed",
                TenantPublicId = tenant.PublicId,
                Detail = $"{current?.Subdomain ?? "(none)"} -> {subdomain}",
                CreatedAt = now,
            });

            // Refused when, between the checks above and here, another tenant took the address or
            // another rename of this tenant committed. Nothing of this one is kept.
            if (!await tenants.TrySaveAsync(ct))
            {
                await transaction.RollbackAsync(ct);
                return CommandResult.Conflict(
                    PlatformProblems.SubdomainTaken,
                    $"\"{subdomain}\" was taken, or this tenant's address was changed, at the same moment. " +
                    "Read the page again and choose an address.");
            }

            await transaction.CommitAsync(ct);
            return CommandResult.Ok(new { tenantId = tenant.PublicId, subdomain });
        }

        private static CommandResult Taken(string subdomain)
            => CommandResult.Conflict(PlatformProblems.SubdomainTaken, TenantAddress.TakenMessage(subdomain));
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/platform/v1/tenants/{tenantId}/address",
            async (
                string tenantId,
                ChangeTenantAddressCommand cmd,
                [FromServices] IOperatorContext operatorContext,
                [FromServices] ITenantService tenants,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new ChangeTenantAddressCommandHandler(tenants, clock);
                return (await handler.Handle(operatorContext.Require().PlatformUserId, tenantId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
