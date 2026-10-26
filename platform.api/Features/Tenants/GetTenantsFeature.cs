using AppPlatform.Api;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Tenants;

/// <summary>
/// Every tenant, with its status and live apps: the console's home page. Commercial state only;
/// nothing here comes from a tenant's own data.
/// </summary>
public static class GetTenantsFeature
{
    public class GetTenantsQueryHandler(ITenantService tenants, ITenantSite site)
    {
        public async Task<CommandResult> Handle(CancellationToken ct = default)
        {
            var summaries = await tenants.ListSummariesAsync(ct);
            return CommandResult.Ok(summaries
                .Select(t => t with { SignInUrl = t.Subdomain is { } s ? site.SignInUrl(s) : null })
                .ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/platform/v1/tenants",
            async (
                [FromServices] IOperatorContext operatorContext,
                [FromServices] ITenantService tenants,
                [FromServices] ITenantSite site,
                CancellationToken ct) =>
            {
                // Operators only: see GetErrorFeedFeature.
                operatorContext.Require();
                return (await new GetTenantsQueryHandler(tenants, site).Handle(ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
