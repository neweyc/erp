using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ledger.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Periods;

public static class GetPeriodsFeature
{
    public record PeriodModel(string CompanyId, string CompanyName, DateOnly? ClosedThrough);

    public class GetPeriodsQueryHandler(ILedgerService ledger)
    {
        public async Task<CommandResult> Handle(Caller caller, CancellationToken ct = default)
        {
            var companies = await ledger.ActiveCompaniesAsync(ct);
            var books = (await ledger.ListBooksAsync(ct)).ToDictionary(b => b.CompanyId);

            // Every active company, including one with no books yet — that is open, not unknown.
            return CommandResult.Ok(companies
                .Select(c => new PeriodModel(c.PublicId, c.Name, books.GetValueOrDefault(c.Id)?.ClosedThrough))
                .ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/ledger/v1/periods",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                CancellationToken ct) =>
            {
                var handler = new GetPeriodsQueryHandler(ledger);
                return (await handler.Handle(callerContext.Require(), ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
