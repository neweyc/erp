using AppPlatform.Core.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core.Services;

/// <summary>
/// Which tenant an anonymous request belongs to.
///
/// Needed only before a session exists: sign-in. Everywhere else the tenant comes from the
/// validated session, and a tenant id in a header or body is an attack rather than a feature.
/// </summary>
public interface ITenantResolver
{
    Task<int?> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>
/// Reads the tenant from the request's host: <c>acme-fire.app.example.com</c> is the tenant whose
/// current address is <c>acme-fire</c>, when the base domain is <c>app.example.com</c>.
///
/// A host that names no current address resolves to nothing, and sign-in refuses. That includes
/// a tenant's former address after a rename: it stays reserved to that tenant but no longer signs
/// anyone in. Nothing here falls back to a default tenant, because "the first tenant" for a host
/// nobody recognizes is a sign-in into somebody else's organization.
/// </summary>
public class EFTenantResolver(CoreDbContext db, string baseDomain) : ITenantResolver
{
    public async Task<int?> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (TenantAddress.SubdomainOf(host, baseDomain) is not { } subdomain)
            return null;

        return await db.TenantSubdomains
            .Where(s => s.Subdomain == subdomain && s.ReplacedAt == null)
            .Select(s => (int?)s.TenantId)
            .FirstOrDefaultAsync(ct);
    }
}
