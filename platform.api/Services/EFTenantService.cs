using AppPlatform.Api;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppPlatform.Platform.Services;

public class EFTenantService(PlatformDbContext db) : ITenantService
{
    public Task<List<TenantSummary>> ListSummariesAsync(CancellationToken ct = default)
        => db.Tenants
            .OrderBy(t => t.Name).ThenBy(t => t.Id)
            .Select(t => new TenantSummary(
                t.PublicId, t.Name, t.Status.ToString(), t.CreatedAt, t.StatusChangedAt,
                db.TenantApps
                    .Where(a => a.TenantId == t.Id && a.RevokedAt == null)
                    .OrderBy(a => a.App)
                    .Select(a => a.App)
                    .ToList(),
                db.TenantSubdomains
                    .Where(s => s.TenantId == t.Id && s.ReplacedAt == null)
                    .Select(s => s.Subdomain)
                    .FirstOrDefault()))
            .ToListAsync(ct);

    public Task<List<GrantRow>> GrantHistoryAsync(int tenantId, CancellationToken ct = default)
        => db.TenantApps
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.GrantedAt).ThenByDescending(a => a.Id)
            .Select(a => new GrantRow(a.App, a.GrantedAt, a.RevokedAt))
            .ToListAsync(ct);

    public async Task<List<AddressRow>> AddressHistoryAsync(int tenantId, CancellationToken ct = default)
    {
        // The current address from its own row; every former span from the periods, so an address
        // the tenant left twice appears twice.
        var current = await db.TenantSubdomains
            .Where(s => s.TenantId == tenantId && s.ReplacedAt == null)
            .Select(s => new AddressRow(s.Subdomain, s.AssignedAt, null))
            .ToListAsync(ct);

        var former = await db.TenantAddressPeriods
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.ReplacedAt).ThenByDescending(p => p.Id)
            .Select(p => new AddressRow(p.Subdomain, p.AssignedAt, p.ReplacedAt))
            .ToListAsync(ct);

        return [.. current, .. former];
    }

    public Task<TenantSubdomain?> CurrentAddressAsync(int tenantId, CancellationToken ct = default)
        => db.TenantSubdomains.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.ReplacedAt == null, ct);

    public Task<TenantSubdomain?> FindAddressAsync(string subdomain, CancellationToken ct = default)
        => db.TenantSubdomains.FirstOrDefaultAsync(s => s.Subdomain == subdomain, ct);

    public Task<Tenant?> FindByPublicIdAsync(string publicId, CancellationToken ct = default)
        => db.Tenants.FirstOrDefaultAsync(t => t.PublicId == publicId, ct);

    public Task<TenantApp?> FindGrantAsync(int tenantId, string app, CancellationToken ct = default)
        => db.TenantApps.FirstOrDefaultAsync(
            a => a.TenantId == tenantId && a.App == app && a.RevokedAt == null, ct);

    public void Add(TenantApp grant) => db.TenantApps.Add(grant);

    public void Add(TenantSubdomain address) => db.TenantSubdomains.Add(address);

    public void Add(TenantAddressPeriod period) => db.TenantAddressPeriods.Add(period);

    public void Audit(PlatformAuditLog entry) => db.AuditLogs.Add(entry);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task<bool> TrySaveAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (DatabaseConflict.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => db.Database.BeginTransactionAsync(ct);
}

public class EFIdempotencyService(PlatformDbContext db) : IIdempotencyService
{
    public Task<string?> FindResponseAsync(string operation, string key, CancellationToken ct = default)
        => db.IdempotencyRecords
            .Where(r => r.Operation == operation && r.Key == key)
            .Select(r => r.ResponseJson)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> TryRecordAsync(
        string operation, string key, string responseJson, PlatformAuditLog audit, CancellationToken ct = default)
    {
        db.AuditLogs.Add(audit);
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Operation = operation,
            Key = key,
            ResponseJson = responseJson,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (DatabaseConflict.IsUniqueViolation(ex))
        {
            // Unique violation: a concurrent retry got there first. Checking before inserting
            // cannot prevent this — both callers would read nothing and both proceed — so the
            // index is the guard and this is its expected outcome, not an error. Clearing drops our
            // audit entry too: the winner wrote the one that describes what happened.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
