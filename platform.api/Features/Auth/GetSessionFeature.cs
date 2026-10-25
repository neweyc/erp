using AppPlatform.Api;
using AppPlatform.Platform.Auth;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Auth;

/// <summary>
/// Who is signed in to the console. The console asks on load: 200 means show the console, 401
/// means show sign-in, and the problem code on a 401 (idle timeout, revoked) says why.
/// </summary>
public static class GetSessionFeature
{
    public record OperatorSessionModel(string Email);

    public class GetSessionQueryHandler
    {
        public CommandResult Handle(OperatorPrincipal principal)
            => CommandResult.Ok(new OperatorSessionModel(principal.Email));
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/platform/v1/auth/session",
            ([FromServices] IOperatorContext operatorContext) =>
                new GetSessionQueryHandler().Handle(operatorContext.Require()).CreateIResult())
            .RequireAuthorization();
    }
}
