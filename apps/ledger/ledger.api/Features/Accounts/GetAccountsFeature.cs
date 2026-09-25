using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ledger.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Accounts;

public static class GetAccountsFeature
{
    public record AccountModel(string AccountId, string Code, string Name, string Type);

    public class GetAccountsQueryHandler(ILedgerService ledger)
    {
        public async Task<CommandResult> Handle(Caller caller, string? companyId, CancellationToken ct = default)
        {
            var company = await CompanyResolver.ResolveAsync(ledger, companyId, ct);
            if (company.Problem is { } problem) return problem;

            var accounts = await ledger.ListAccountsAsync(company.Company!.Id, ct);

            return CommandResult.Ok(accounts
                .Select(a => new AccountModel(a.PublicId, a.Code, a.Name, a.Type.ToString()))
                .ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/ledger/v1/accounts",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                CancellationToken ct,
                string? companyId = null) =>
            {
                var handler = new GetAccountsQueryHandler(ledger);
                return (await handler.Handle(callerContext.Require(), companyId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
