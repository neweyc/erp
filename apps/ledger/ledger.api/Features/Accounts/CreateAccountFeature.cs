using System.Text.RegularExpressions;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ids;
using AppPlatform.Ledger.Data;
using AppPlatform.Ledger.Services;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Accounts;

public static partial class CreateAccountFeature
{
    public record CreateAccountCommand(string? Code, string? Name, string? Type, string? CompanyId);

    /// <summary>Matches the database check constraint on ledger.account.code.</summary>
    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.-]{0,19}$")]
    private static partial Regex AccountCode();

    public class CreateAccountCommandHandler(ILedgerService ledger, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(Caller caller, CreateAccountCommand cmd, CancellationToken ct = default)
        {
            var code = cmd.Code?.Trim() ?? "";
            var name = cmd.Name?.Trim();

            if (!AccountCode().IsMatch(code))
            {
                return CommandResult.Invalid(LedgerProblems.ValidationFailed,
                    "An account code is 1–20 letters, digits, dots or dashes, starting with a letter or digit.");
            }

            if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "A name of up to 200 characters is required.");

            // An exact name, case-insensitively. Not Enum.TryParse, which also accepts "4" and
            // comma lists — "asset,liability" parses to 1, which is Liability.
            var typeName = Enum.GetNames<AccountType>()
                .FirstOrDefault(n => string.Equals(n, cmd.Type?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (typeName is null)
            {
                return CommandResult.Invalid(LedgerProblems.ValidationFailed,
                    "Type must be one of: asset, liability, equity, revenue, expense.");
            }

            var type = Enum.Parse<AccountType>(typeName);

            var company = await CompanyResolver.ResolveAsync(ledger, cmd.CompanyId, ct);
            if (company.Problem is { } problem) return problem;

            var account = new Account
            {
                PublicId = PublicId.New("acct").ToString(),
                CompanyId = company.Company!.Id,
                Code = code,
                Name = name,
                Type = type,
                CreatedAt = clock.GetUtcNow(),
            };

            // The company's first account opens its books, so they exist before anything is posted —
            // every journal entry keys to them, and the period check locks them.
            if (await ledger.FindBooksAsync(account.CompanyId, ct) is null)
                ledger.AddBooks(new Books { PublicId = PublicId.New("bk").ToString(), CompanyId = account.CompanyId });

            ledger.AddAccount(account);
            outbox.AddEvent("account", account.PublicId, 1, "account.created",
                $$"""{"accountId":"{{account.PublicId}}"}""");

            try
            {
                await ledger.SaveAsync(ct);
            }
            catch (Exception ex) when (DatabaseConflict.IsUniqueViolation(ex))
            {
                // The unique index is the guard; a preflight read could not see a concurrent insert.
                // Two first accounts created at once can also both try to open the books — the loser
                // is told to retry, and finds them open.
                return ex.InnerException is Npgsql.PostgresException { ConstraintName: { } constraint }
                       && constraint.StartsWith("ak_books", StringComparison.Ordinal)
                    ? CommandResult.Conflict(LedgerProblems.ConcurrentChange, "The books were being opened at the same moment. Try again.")
                    : CommandResult.Conflict(LedgerProblems.DuplicateAccountCode, $"This company already has an account {code}.");
            }

            return CommandResult.Ok(new { accountId = account.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/ledger/v1/accounts",
            async (
                CreateAccountCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new CreateAccountCommandHandler(ledger, outbox, clock);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
