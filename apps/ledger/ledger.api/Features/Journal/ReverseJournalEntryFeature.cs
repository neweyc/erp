using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ids;
using AppPlatform.Ledger.Services;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Journal;

/// <summary>
/// The only way to correct a posted entry. A reversal is a new entry with every line negated,
/// dated when the correction is made, pointing back at the original — so both the mistake and its
/// correction stay on the record, and the periods they fall in are both still true.
/// </summary>
public static class ReverseJournalEntryFeature
{
    public record ReverseJournalEntryCommand(DateOnly? EntryDate, string? Memo);

    public class ReverseJournalEntryCommandHandler(ILedgerService ledger, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string entryPublicId, ReverseJournalEntryCommand cmd, CancellationToken ct = default)
        {
            var original = PublicId.TryParse(entryPublicId, "je", out _)
                ? await ledger.FindEntryWithLinesAsync(entryPublicId, ct)
                : null;

            if (original is null)
                return CommandResult.NotFound(LedgerProblems.NotFound, "No such entry.");

            // Undoing an undo is a third entry that restores the first; allowing it makes "is this
            // entry in effect?" depend on counting a chain. Post the entry again instead.
            if (original.ReversesEntryId is not null)
            {
                return CommandResult.Conflict(LedgerProblems.CannotReverseAReversal,
                    "This entry is a reversal. To restore the original, post it again.");
            }

            if (await ledger.IsReversedAsync(original.Id, ct))
                return CommandResult.Conflict(LedgerProblems.AlreadyReversed, "This entry has already been reversed.");

            var entryDate = cmd.EntryDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

            // A correction cannot land in a period before the thing it corrects.
            if (entryDate < original.EntryDate)
            {
                return CommandResult.Invalid(LedgerProblems.ValidationFailed,
                    "A reversal cannot be dated before the entry it reverses.");
            }

            // The reversal is dated in its own period, which must be open — the original's may not be.
            if (await Posting.PeriodProblemAsync(ledger, original.CompanyId, entryDate, ct) is { } closed)
                return closed;

            var memo = cmd.Memo?.Trim() is { Length: > 0 } given
                ? given
                : $"Reversal of {original.FiscalYear}-{original.Number}: {original.Memo}";

            if (memo.Length > 500)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "A memo is limited to 500 characters.");

            var reversal = original.Lines
                .OrderBy(l => l.LineNumber)
                // checked: long.MinValue has no negation. It is refused on the way in (handler and
                // database), so this cannot throw — and if that guard ever went, it would throw, not wrap.
                .Select(l => new Posting.Line(l.AccountId, checked(-l.AmountMinor), l.Memo))
                .ToList();

            try
            {
                var entry = await Posting.PostAsync(
                    ledger, outbox, clock, original.CompanyId, entryDate, memo, original.Currency,
                    reversal, reversesEntryId: original.Id, ct);

                return CommandResult.Ok(new { entryId = entry.PublicId, number = entry.Number, fiscalYear = entry.FiscalYear });
            }
            catch (Exception ex) when (Posting.IsPeriodClosed(ex))
            {
                return CommandResult.Conflict(LedgerProblems.PeriodClosed,
                    "The books were closed for that date while reversing. Date the reversal after the close.");
            }
            catch (Exception ex) when (DatabaseConflict.IsUniqueViolation(ex))
            {
                // Two reversals raced past the check above; the unique index let one through.
                return CommandResult.Conflict(LedgerProblems.AlreadyReversed, "This entry has already been reversed.");
            }
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/ledger/v1/entries/{entryId}/reversal",
            async (
                string entryId,
                ReverseJournalEntryCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new ReverseJournalEntryCommandHandler(ledger, outbox, clock);
                return (await handler.Handle(callerContext.Require(), entryId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
