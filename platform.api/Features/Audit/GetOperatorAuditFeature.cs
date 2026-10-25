using AppPlatform.Api;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Audit;

/// <summary>
/// The operator audit log: sign-ins (failed ones too), sign-outs, tenants created, suspended,
/// resumed and retired, and apps licensed and revoked. Separate from every tenant's own audit by
/// design; nothing a customer did appears here.
/// </summary>
public static class GetOperatorAuditFeature
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public class GetOperatorAuditQueryHandler(IAuditLogService audit)
    {
        public async Task<CommandResult> Handle(int? limit, CancellationToken ct = default)
            // Clamped, not refused, as on the error feed.
            => CommandResult.Ok(await audit.RecentAsync(null, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct));
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/platform/v1/audit",
            async (
                [FromServices] IOperatorContext operatorContext,
                [FromServices] IAuditLogService audit,
                CancellationToken ct,
                int? limit = null) =>
            {
                // Operators only: see GetErrorFeedFeature.
                operatorContext.Require();
                return (await new GetOperatorAuditQueryHandler(audit).Handle(limit, ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
