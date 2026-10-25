using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Tenants;

/// <summary>
/// One tenant: its status, every app grant it has ever had, and the operator actions taken on it.
/// What an operator needs before suspending, retiring or licensing, on one page.
/// </summary>
public static class GetTenantFeature
{
    /// <summary>Enough history to see what happened recently; the full log is on the audit page.</summary>
    public const int RecentActions = 50;

    public record TenantDetailModel(
        string TenantId, string Name, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StatusChangedAt,
        List<string> Apps, List<GrantRow> Grants, List<AuditRow> Actions);

    public class GetTenantQueryHandler(ITenantService tenants, IAuditLogService audit)
    {
        public async Task<CommandResult> Handle(string tenantPublicId, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(tenantPublicId, "ten", out _))
                return CommandResult.NotFound(PlatformProblems.NotFound, "That is not a tenant id.");

            var tenant = await tenants.FindByPublicIdAsync(tenantPublicId, ct);
            if (tenant is null)
                return CommandResult.NotFound(PlatformProblems.NotFound, "No such tenant.");

            var grants = await tenants.GrantHistoryAsync(tenant.Id, ct);

            return CommandResult.Ok(new TenantDetailModel(
                tenant.PublicId, tenant.Name, tenant.Status.ToString(), tenant.CreatedAt, tenant.StatusChangedAt,
                // Derived from the history just read, so the two can never disagree on one page.
                grants.Where(g => g.RevokedAt is null).Select(g => g.App).Order().ToList(),
                grants,
                await audit.RecentAsync(tenant.PublicId, RecentActions, ct)));
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/platform/v1/tenants/{tenantId}",
            async (
                string tenantId,
                [FromServices] IOperatorContext operatorContext,
                [FromServices] ITenantService tenants,
                [FromServices] IAuditLogService audit,
                CancellationToken ct) =>
            {
                // Operators only: see GetErrorFeedFeature.
                operatorContext.Require();
                return (await new GetTenantQueryHandler(tenants, audit).Handle(tenantId, ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
