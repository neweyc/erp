using AppPlatform.Audit;

namespace AppPlatform.Ledger.Data;

/// <summary>
/// One company's books: today, just how far they are closed.
///
/// A closed period refuses every posting dated on or before <see cref="ClosedThrough"/>, at the
/// database as well as in the handlers. Closing only ever moves forward — there is no reopen. A
/// mistake found in a closed period is corrected by an entry dated in an open one, which is how a
/// closed period stays exactly what was reported for it.
///
/// Created with the company's first account, so it exists before anything can be posted: every
/// journal entry has a foreign key to it. Audited, which is what records who closed a period, and when.
/// </summary>
public class Books : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public int CompanyId { get; set; }
    public string PublicId { get; set; } = "";

    /// <summary>The last closed day, or null while nothing has been closed.</summary>
    public DateOnly? ClosedThrough { get; set; }

    /// <summary>Advanced by each close: the sequence number of its event, and a concurrency token.</summary>
    public long Version { get; set; } = 1;
}
