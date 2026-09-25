using AppPlatform.Tenancy;

namespace AppPlatform.Tickets.Data;

/// <summary>
/// <c>core_v1.employee</c> — core's published contract, read-only.
///
/// Mapped with ToView, never ToTable: a published view is a contract, and mapping it as a table
/// hands this app a write path into data it does not own. The write would fail at the grant in
/// production rather than here, which is the wrong place to find out.
///
/// Tenant-scoped like any other entity, because the view carries tenant_id — so the ordinary
/// global query filter applies with no new machinery.
/// </summary>
public class PublishedEmployee : ITenantScoped
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public int CompanyId { get; set; }
    public string PublicId { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "";
}
