using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ids;
using AppPlatform.Ledger.Services;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Journal;

public static partial class PostJournalEntryFeature
{
    public record LineCommand(string? AccountId, long AmountMinor, string? Memo);

    public record PostJournalEntryCommand(
        DateOnly? EntryDate, string? Memo, string? Currency, List<LineCommand>? Lines, string? IdempotencyKey);

    /// <summary>
    /// A three-letter code in ISO 4217 FORM, matching the database check constraint. It is not checked
    /// against the ISO list, and the minor unit is taken to be the caller's — which currencies are
    /// supported, and their decimal places, is an open question (docs/open-questions.md).
    /// </summary>
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    /// <summary>A generous ceiling that keeps a request from asking for an unbounded write.</summary>
    public const int MaxLines = 500;

    public class PostJournalEntryCommandHandler(ILedgerService ledger, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(Caller caller, PostJournalEntryCommand cmd, CancellationToken ct = default)
        {
            var memo = cmd.Memo?.Trim();
            var lines = cmd.Lines ?? [];

            // Required, because posting money is exactly the request a client retries after a timeout —
            // and without a key the retry is indistinguishable from a second, genuine entry.
            var idempotencyKey = cmd.IdempotencyKey?.Trim();
            if (idempotencyKey is not { Length: > 0 and <= 100 })
            {
                return CommandResult.Invalid(LedgerProblems.ValidationFailed,
                    "An idempotency key of up to 100 characters is required, unique to this entry.");
            }

            if (cmd.EntryDate is not { } entryDate)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "An entry date is required.");

            if (string.IsNullOrWhiteSpace(memo) || memo.Length > 500)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "A memo of up to 500 characters is required.");

            if (cmd.Currency is null || !CurrencyCode().IsMatch(cmd.Currency))
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "Currency must be a three-letter ISO code, e.g. USD.");

            if (lines.Count > MaxLines)
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, $"An entry is limited to {MaxLines} lines.");

            if (lines.Any(l => l.Memo is { Length: > 500 }))
                return CommandResult.Invalid(LedgerProblems.ValidationFailed, "A line memo is limited to 500 characters.");

            // Balance first: it needs no database, and it is the rule most worth a precise message.
            var amounts = lines.Select(l => new Posting.Line(Guid.Empty, l.AmountMinor, null)).ToList();
            if (Posting.Problem(amounts) is { } unbalanced)
                return CommandResult.Invalid(LedgerProblems.Unbalanced, unbalanced);

            // Resolve, never trust: every account id is user input until it is found under the
            // tenant filter. The prefix check first, so an entry id passed as an account id is
            // reported as what it is rather than as a missing account.
            if (lines.Any(l => !PublicId.TryParse(l.AccountId, "acct", out _)))
                return CommandResult.Invalid(LedgerProblems.AccountNotFound, "Every line needs an account id (acct_…).");

            var accounts = (await ledger.FindAccountsAsync(lines.Select(l => l.AccountId!), ct))
                .ToDictionary(a => a.PublicId);

            if (lines.FirstOrDefault(l => !accounts.ContainsKey(l.AccountId!)) is { } missing)
                return CommandResult.Invalid(LedgerProblems.AccountNotFound, $"No such account: {missing.AccountId}.");

            // One entry, one company's books. Company is an accounting dimension, so an entry that
            // touched two would put half a transaction in each set of books.
            var companies = accounts.Values.Select(a => a.CompanyId).Distinct().ToList();
            if (companies.Count != 1)
            {
                return CommandResult.Invalid(LedgerProblems.AccountsSpanCompanies,
                    "Every line of an entry must use accounts of the same company.");
            }

            var fingerprint = Fingerprint(entryDate, memo, cmd.Currency, lines);

            // A retry of a post that already succeeded: answer with that entry, post nothing.
            if (await ledger.FindEntryByIdempotencyKeyAsync(idempotencyKey, ct) is { } earlier)
                return Replay(earlier, fingerprint);

            // After the replay check: a retry of a post that succeeded before the period closed
            // still gets its original answer rather than a refusal.
            if (await Posting.PeriodProblemAsync(ledger, companies[0], entryDate, ct) is { } closed)
                return closed;

            try
            {
                var entry = await Posting.PostAsync(
                    ledger, outbox, clock, companies[0], entryDate, memo, cmd.Currency,
                    [.. lines.Select(l => new Posting.Line(accounts[l.AccountId!].Id, l.AmountMinor, l.Memo?.Trim()))],
                    reversesEntryId: null, ct, idempotencyKey, fingerprint);

                return Posted(entry, replayed: false);
            }
            catch (Exception ex) when (Posting.IsPeriodClosed(ex))
            {
                // A close committed between the check above and this post; the database refused it.
                return CommandResult.Conflict(LedgerProblems.PeriodClosed,
                    "The books were closed for that date while posting. Date the entry after the close.");
            }
            catch (Exception ex) when (DatabaseConflict.IsUniqueViolation(ex))
            {
                // Two concurrent retries: both passed the lookup, the unique index let one post.
                // The loser's transaction rolled back — returning its number — and it answers with
                // the winner's entry, exactly as a later retry would.
                var winner = await ledger.FindEntryByIdempotencyKeyAsync(idempotencyKey, ct);
                if (winner is null) throw;
                return Replay(winner, fingerprint);
            }
        }

        private static CommandResult Replay(Data.JournalEntry earlier, string fingerprint)
            => earlier.RequestFingerprint == fingerprint
                ? Posted(earlier, replayed: true)
                : CommandResult.Conflict(LedgerProblems.IdempotencyKeyReused,
                    "This idempotency key was already used for a different entry.");

        private static CommandResult Posted(Data.JournalEntry entry, bool replayed)
            => CommandResult.Ok(new { entryId = entry.PublicId, number = entry.Number, fiscalYear = entry.FiscalYear, replayed });

        /// <summary>
        /// What the request asked for, as a hash — so a key reused for a different entry is caught.
        /// Account ids as the caller sent them: the same request always yields the same fingerprint.
        /// </summary>
        private static string Fingerprint(DateOnly date, string memo, string currency, List<LineCommand> lines)
        {
            var canonical = JsonSerializer.Serialize(new
            {
                date, memo, currency,
                lines = lines.Select(l => new { l.AccountId, l.AmountMinor, memo = l.Memo?.Trim() }),
            });

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/ledger/v1/entries",
            async (
                PostJournalEntryCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new PostJournalEntryCommandHandler(ledger, outbox, clock);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
