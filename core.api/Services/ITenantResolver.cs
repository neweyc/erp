using AppPlatform.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core.Services;

/// <summary>
/// Which tenant an anonymous request belongs to.
///
/// Needed only before a session exists — sign-in, invite acceptance, password reset. Everywhere
/// else the tenant comes from the validated session, and a tenant id in a header or body is an
/// attack rather than a feature.
/// </summary>
public interface ITenantResolver
{
    Task<int?> ResolveAsync(string host, CancellationToken ct = default);
}

public class EFTenantResolver(CoreDbContext db, IConfiguration config) : ITenantResolver
{
    public async Task<int?> ResolveAsync(string host, CancellationToken ct = default)
    {
        // Single-tenant deployments and local development pin the tenant by configuration,
        // because there is no meaningful hostname to read.
        if (config["Tenant:PublicId"] is { Length: > 0 } pinned)
        {
            return await db.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.PublicId == pinned)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
        }

        // Host-based: acme.example.com -> the tenant whose subdomain is "acme". Deliberately not
        // implemented by guessing — an unmatched host resolves to nothing and sign-in fails,
        // rather than falling back to "the first tenant", which would be a cross-tenant login.
        return null;
    }
}
