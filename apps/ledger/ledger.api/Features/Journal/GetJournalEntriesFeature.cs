using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ledger.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Ledger.Features.Journal;

public static class GetJournalEntriesFeature
{
    public record LineModel(int LineNumber, string AccountId, string AccountCode, long AmountMinor, string? Memo);

    public record EntryModel(
        string EntryId, int FiscalYear, int Number, DateOnly EntryDate, string Memo, string Currency,
        DateTimeOffset PostedAt, string? ReversesEntryId, string? ReversedByEntryId, List<LineModel> Lines);

    public class GetJournalEntriesQueryHandler(ILedgerService ledger)
    {
        public async Task<CommandResult> Handle(
            Caller caller, DateOnly? from, DateOnly? to, CancellationToken ct = default)
        {
            var entries = await ledger.ListEntriesWithLinesAsync(from, to, ct);
            var accounts = (await ledger.ListAccountsAsync(null, ct)).ToDictionary(a => a.Id);

            // Both directions of a reversal, so a reader sees at a glance that an entry is no
            // longer in effect — without which it looks like a live posting. Looked up rather than
            // taken from this page, because either side may be dated outside the requested range.
            var reversedBy = await ledger.FindReversalsOfAsync(entries.Select(e => e.Id), ct);
            var reversed = await ledger.FindEntryPublicIdsAsync(
                entries.Where(e => e.ReversesEntryId is not null).Select(e => e.ReversesEntryId!.Value), ct);

            return CommandResult.Ok(entries.Select(e => new EntryModel(
                e.PublicId, e.FiscalYear, e.Number, e.EntryDate, e.Memo, e.Currency, e.PostedAt,
                e.ReversesEntryId is { } r ? reversed[r] : null,
                reversedBy.GetValueOrDefault(e.Id),
                [.. e.Lines.OrderBy(l => l.LineNumber).Select(l => new LineModel(
                    l.LineNumber, accounts[l.AccountId].PublicId, accounts[l.AccountId].Code, l.AmountMinor, l.Memo))]))
                .ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/ledger/v1/entries",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] ILedgerService ledger,
                CancellationToken ct,
                // Read from the query string and honoured, never accepted and ignored.
                DateOnly? from = null,
                DateOnly? to = null) =>
            {
                var handler = new GetJournalEntriesQueryHandler(ledger);
                return (await handler.Handle(callerContext.Require(), from, to, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Ledger);
    }
}
