using AppPlatform.Audit;
using AppPlatform.Ids;
using AppPlatform.Tenancy;

namespace AppPlatform.Core.Data;

/// <summary>
/// A legal entity: its own books, fiscal calendar, tax registration, filings.
///
/// Every tenant gets exactly one at provisioning and there is no UI for the concept. The
/// column exists now because retrofitting it means asking a live customer which legal entity
/// each historical employee belonged to — a question they usually cannot answer. The schema is
/// cheap; the data archaeology is not.
///
/// A company is an ACCOUNTING dimension, never an access boundary. If entity A's staff must
/// not see entity B's data, that is two tenants.
/// </summary>
public class Company : IAuditable
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string PublicId { get; set; } = "";
    public required string Name { get; set; }
    public string? LegalName { get; set; }
    public bool Active { get; set; } = true;
}
