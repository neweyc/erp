using AppPlatform.Audit;
using AppPlatform.Tenancy;

namespace AppPlatform.Ledger.Data;

/// <summary>
/// A posted journal entry: two or more lines that sum to zero.
///
/// Append-only. There is no draft state and no edit: an entry exists only once it is posted, and a
/// mistake is corrected by posting its reversal. The database refuses an UPDATE or DELETE as well
/// as the code (see docs/ledger.md).
/// </summary>
public class JournalEntry : IAuditable, IAppendOnly
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public int CompanyId { get; set; }
    public string PublicId { get; set; } = "";

    /// <summary>
    /// Dense per company per year: 1, 2, 3 … with no gaps. Allocated inside the posting transaction,
    /// so a post that fails gives its number back.
    /// </summary>
    public int Number { get; set; }

    /// <summary>The calendar year of <see cref="EntryDate"/> — the series <see cref="Number"/> belongs to.</summary>
    public int FiscalYear { get; set; }

    /// <summary>The accounting date: which period the entry belongs to, not when it was typed in.</summary>
    public DateOnly EntryDate { get; set; }

    public required string Memo { get; set; }

    /// <summary>ISO 4217, e.g. "USD". One currency per entry; amounts are in its minor unit.</summary>
    public required string Currency { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    /// <summary>The entry this one reverses, if it is a reversal. An entry is reversed at most once.</summary>
    public Guid? ReversesEntryId { get; set; }

    /// <summary>
    /// The caller's key for this post, unique per tenant. A request retried after its response was
    /// lost returns the entry it already made instead of posting the same money twice.
    /// Null for reversals, which are made at most once per entry anyway.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// SHA-256 of the request that used <see cref="IdempotencyKey"/>, so reusing a key for a
    /// DIFFERENT entry is refused rather than silently answered with the first one.
    /// </summary>
    public string? RequestFingerprint { get; set; }

    /// <summary>
    /// How many lines the entry has, fixed when it is posted. This is what SEALS an entry at the
    /// database: line numbers must fall in 1..LineCount and are unique, and the count must be exact at
    /// commit — so once posted, every slot is filled and no further line can ever be added.
    /// </summary>
    public int LineCount { get; set; }

    public List<JournalLine> Lines { get; set; } = [];
}

/// <summary>
/// One side of an entry. Signed: a debit is positive, a credit negative, so "balanced" is simply
/// "sums to zero" and the rule cannot be half-applied.
/// </summary>
public class JournalLine : ITenantScoped, IAppendOnly
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }

    /// <summary>
    /// Always the entry's company. Carried on the line so the database can refuse, by foreign key,
    /// a line whose account belongs to another company's books.
    /// </summary>
    public int CompanyId { get; set; }

    public Guid EntryId { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>Position within the entry, from 1, so the lines read back in the order they were posted.</summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// In the currency's minor unit (cents for USD). An integer, never a float or a decimal that
    /// could round: money that does not add up exactly is the defect this app exists to rule out.
    /// </summary>
    public long AmountMinor { get; set; }

    public string? Memo { get; set; }
}

/// <summary>
/// The last number issued in one (tenant, company, year) series. Mutable by design — it is the
/// counter — and written only by <c>EFLedgerService.NextEntryNumberAsync</c>.
/// </summary>
public class EntrySequence : ITenantScoped
{
    public int TenantId { get; set; }
    public int CompanyId { get; set; }
    public int FiscalYear { get; set; }
    public int LastNumber { get; set; }
}

/// <summary>
/// <c>core_v1.company</c> — core's published contract, read-only. Mapped with ToView, never
/// ToTable: this app has no grant on core's tables and must never hold a write path into them.
/// </summary>
public class PublishedCompany : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string PublicId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; }
}
