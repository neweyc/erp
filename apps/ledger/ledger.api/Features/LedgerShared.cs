using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Ledger.Data;
using AppPlatform.Ledger.Services;

namespace AppPlatform.Ledger.Features;

/// <summary>Stable codes the UI and integrations branch on — never a message match.</summary>
public static class LedgerProblems
{
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string CompanyNotFound = "company_not_found";
    public const string CompanyRequired = "company_required";
    public const string DuplicateAccountCode = "duplicate_account_code";
    public const string AccountNotFound = "account_not_found";
    public const string AccountsSpanCompanies = "accounts_span_companies";
    public const string Unbalanced = "unbalanced";
    public const string AlreadyReversed = "already_reversed";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string CannotReverseAReversal = "cannot_reverse_a_reversal";
}

/// <summary>
/// Which company's books a request means. Shared by every feature that takes a company, so they
/// cannot drift into resolving it by different rules.
///
/// Resolve, never trust: a company id in a request is looked up through the published view under
/// the tenant filter, and one that is not found is a validation failure — never a new row.
/// </summary>
public static class CompanyResolver
{
    public record Resolution(PublishedCompany? Company, CommandResult? Problem);

    public static async Task<Resolution> ResolveAsync(
        ILedgerService ledger, string? companyPublicId, CancellationToken ct)
    {
        if (companyPublicId is { Length: > 0 })
        {
            var found = PublicId.TryParse(companyPublicId, "co", out _)
                ? await ledger.FindCompanyAsync(companyPublicId, ct)
                : null;

            return found is { Active: true }
                ? new(found, null)
                : new(null, CommandResult.Invalid(LedgerProblems.CompanyNotFound, "No such company."));
        }

        // Omitted: fine while the tenant has exactly one company, which today is always. Guessing
        // among several would post to the wrong legal entity's books.
        var companies = await ledger.ActiveCompaniesAsync(ct);

        return companies.Count switch
        {
            1 => new(companies[0], null),
            0 => new(null, CommandResult.Invalid(LedgerProblems.CompanyNotFound, "This organisation has no company.")),
            _ => new(null, CommandResult.Invalid(LedgerProblems.CompanyRequired, "Say which company's books.")),
        };
    }
}

/// <summary>
/// Posting: the one place an entry is written. Post and reverse both come through here, so the
/// balance rule, the numbering, and the event cannot differ between them.
/// </summary>
public static class Posting
{
    public record Line(Guid AccountId, long AmountMinor, string? Memo);

    /// <summary>
    /// Null when the lines may be posted; otherwise why not. The database refuses an unbalanced
    /// entry too — this exists so the caller gets a problem code instead of a failed commit.
    /// </summary>
    public static string? Problem(IReadOnlyList<Line> lines)
    {
        if (lines.Count < 2) return "An entry needs at least two lines.";
        if (lines.Any(l => l.AmountMinor == 0)) return "A line cannot be zero.";

        // The one 64-bit value with no negation: an entry holding it could never be reversed.
        if (lines.Any(l => l.AmountMinor == long.MinValue)) return "An amount is out of range.";

        // Summed in 128 bits: two lines of long.MaxValue must be reported as unbalanced, not wrap
        // around to a sum that happens to look fine.
        Int128 sum = 0;
        foreach (var line in lines) sum += line.AmountMinor;

        return sum == 0 ? null : "Debits and credits must be equal: the lines must sum to zero.";
    }

    /// <summary>
    /// Numbers, writes and commits one entry in a single transaction. The caller has already
    /// validated the lines and resolved every account to the given company.
    /// </summary>
    public static async Task<JournalEntry> PostAsync(
        ILedgerService ledger, Outbox.IOutbox outbox, TimeProvider clock,
        int companyId, DateOnly entryDate, string memo, string currency,
        IReadOnlyList<Line> lines, Guid? reversesEntryId, CancellationToken ct,
        string? idempotencyKey = null, string? requestFingerprint = null)
    {
        // Numbering and writing share one transaction: the number is gapless only because a post
        // that fails anywhere below gives its number back when this rolls back.
        await using var transaction = await ledger.BeginTransactionAsync(ct);

        var entry = new JournalEntry
        {
            PublicId = PublicId.New("je").ToString(),
            CompanyId = companyId,
            FiscalYear = entryDate.Year,
            Number = await ledger.NextEntryNumberAsync(companyId, entryDate.Year, ct),
            EntryDate = entryDate,
            Memo = memo,
            Currency = currency,
            PostedAt = clock.GetUtcNow(),
            ReversesEntryId = reversesEntryId,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            LineCount = lines.Count,
            Lines = [.. lines.Select((l, i) => new JournalLine
            {
                CompanyId = companyId,
                AccountId = l.AccountId,
                LineNumber = i + 1,
                AmountMinor = l.AmountMinor,
                Memo = l.Memo,
            })],
        };

        ledger.AddEntry(entry);

        // Version 1 always: an entry is never changed, so it has exactly one version.
        outbox.AddEvent("journal_entry", entry.PublicId, 1, "journal_entry.posted",
            $$"""{"entryId":"{{entry.PublicId}}"}""");

        await ledger.SaveAsync(ct);
        await transaction.CommitAsync(ct);

        return entry;
    }
}
