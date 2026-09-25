using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ledger.Services;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Periods;

/// <summary>
/// Closes a company's books through a date. Afterwards nothing can be posted on or before it — the
/// handlers refuse, and so does the database, under a lock that stops a post and a close interleaving.
///
/// Forward only. There is no reopen: a closed period stays exactly what was reported for it, and a
/// mistake found in it is corrected by an entry in an open period.
/// </summary>
public static class ClosePeriodFeature
{
    public record ClosePeriodCommand(DateOnly? ClosedThrough, string? CompanyId);

    /// <summary>
    /// Who may close. Declared here because packages/auth has no role or policy model yet
    /// (CLAUDE.md: Roles.* and Policy* constants, not built); move it there when that exists.
    /// </summary>
    public static readonly string[] RolesThatMayClose = ["admin"];

    public class ClosePeriodCommandHandler(ILedgerService ledger, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(Caller caller, ClosePeriodCommand cmd, CancellationToken ct = default)
        {
            if (!RolesThatMayClose.Contains(caller.Role))
                return CommandResult.Forbidden(LedgerProblems.NotPermitted, "Only an administrator can close the books.");

            if (cmd.ClosedThrough is not { } through)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "A closing date is required.");

            // Closing the future would block today's postings for a period nobody has finished.
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (through >= today)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "Only a date before today can be closed.");

            var company = await CompanyResolver.ResolveAsync(ledger, cmd.CompanyId, ct);
            if (company.Problem is { } problem) return problem;

            // No books means no accounts, and so nothing that could have been posted.
            var books = await ledger.FindBooksAsync(company.Company!.Id, ct);
            if (books is null)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "This company has no accounts yet.");

            if (books.ClosedThrough is { } already && through <= already)
            {
                return CommandResult.Conflict(LedgerProblems.PeriodClosed,
                    $"The books are already closed through {already:yyyy-MM-dd}. A close only moves forward.");
            }

            books.ClosedThrough = through;
            books.Version++;

            outbox.AddEvent("books", books.PublicId, books.Version, "books.period_closed",
                $$"""{"booksId":"{{books.PublicId}}","closedThrough":"{{through:yyyy-MM-dd}}"}""");

            try
            {
                await ledger.SaveAsync(ct);
            }
            catch (Exception ex) when (DatabaseConflict.IsLostRace(ex))
            {
                // Another close landed first; Version is the concurrency token.
                return CommandResult.Conflict(LedgerProblems.ConcurrentChange,
                    "The books were closed by someone else at the same moment. Reload and try again.");
            }

            return CommandResult.Ok(new { companyId = company.Company.PublicId, closedThrough = through });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/ledger/v1/periods/close",
            async (
                ClosePeriodCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new ClosePeriodCommandHandler(ledger, outbox, clock);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
