using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ledger.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Journal;

/// <summary>
/// Every account's balance as of a date, debits in one column and credits in the other. Because
/// every posted entry sums to zero, the two columns are equal — which is the point of printing
/// them: a trial balance that does not balance means the ledger is broken.
///
/// Amounts are decimal here, not long. Every line fits in 64 bits, but a balance summed across many
/// entries, or a column total across many accounts, need not.
/// </summary>
public static class GetTrialBalanceFeature
{
    public record AccountBalance(
        string AccountId, string Code, string Name, string Type, decimal DebitMinor, decimal CreditMinor);

    /// <summary>One per currency: amounts in different currencies are never summed together.</summary>
    public record CurrencyBalance(
        string Currency, List<AccountBalance> Accounts, decimal TotalDebitMinor, decimal TotalCreditMinor);

    public record TrialBalanceModel(string CompanyId, DateOnly AsOf, List<CurrencyBalance> Currencies);

    public class GetTrialBalanceQueryHandler(ILedgerService ledger, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Caller caller, DateOnly? asOf, string? companyId, CancellationToken ct = default)
        {
            var company = await CompanyResolver.ResolveAsync(ledger, companyId, ct);
            if (company.Problem is { } problem) return problem;

            var date = asOf ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var balances = await ledger.TrialBalanceAsync(company.Company!.Id, date, ct);

            var currencies = balances
                // A zero balance is not a line on a trial balance.
                .Where(b => b.BalanceMinor != 0)
                .GroupBy(b => b.Currency)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var rows = g
                        .OrderBy(b => b.Code, StringComparer.Ordinal)
                        // Positive is a debit balance, negative a credit balance — the sign rule
                        // every line was posted with.
                        .Select(b => new AccountBalance(
                            b.AccountPublicId, b.Code, b.Name, b.Type.ToString(),
                            DebitMinor: Math.Max(b.BalanceMinor, 0),
                            CreditMinor: Math.Max(-b.BalanceMinor, 0)))
                        .ToList();

                    return new CurrencyBalance(
                        g.Key, rows, rows.Sum(r => r.DebitMinor), rows.Sum(r => r.CreditMinor));
                })
                .ToList();

            return CommandResult.Ok(new TrialBalanceModel(company.Company.PublicId, date, currencies));
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/ledger/v1/trial-balance",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                [FromServices] TimeProvider clock,
                CancellationToken ct,
                DateOnly? asOf = null,
                string? companyId = null) =>
            {
                var handler = new GetTrialBalanceQueryHandler(ledger, clock);
                return (await handler.Handle(callerContext.Require(), asOf, companyId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
