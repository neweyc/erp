using AppPlatform.Api;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Auth;

/// <summary>
/// Who is signed in to the console. The console asks on load: 200 means show the console, 401
/// means show sign-in, and the problem code on a 401 (idle timeout, revoked) says why.
/// </summary>
public static class GetSessionFeature
{
    /// <param name="TenantBaseUrl">Where tenants' addresses sit, so the console can show the address
    /// an operator is choosing before it is created.</param>
    public record OperatorSessionModel(string Email, string TenantBaseUrl);

    public class GetSessionQueryHandler(TenantSite site)
    {
        public CommandResult Handle(OperatorPrincipal principal)
            => CommandResult.Ok(new OperatorSessionModel(principal.Email, site.BaseUrl.GetLeftPart(UriPartial.Authority)));
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/platform/v1/auth/session",
            ([FromServices] IOperatorContext operatorContext, [FromServices] TenantSite site) =>
                new GetSessionQueryHandler(site).Handle(operatorContext.Require()).CreateIResult())
            .RequireAuthorization();
    }
}
