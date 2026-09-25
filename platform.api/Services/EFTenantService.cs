using AppPlatform.Api;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform.Services;

public class EFTenantService(PlatformDbContext db) : ITenantService
{
    public Task<Tenant?> FindByPublicIdAsync(string publicId, CancellationToken ct = default)
        => db.Tenants.FirstOrDefaultAsync(t => t.PublicId == publicId, ct);

    public Task<List<Tenant>> ListAsync(CancellationToken ct = default)
        => db.Tenants.OrderBy(t => t.Name).ThenBy(t => t.Id).ToListAsync(ct);

    public Task<List<string>> LicensedAppsAsync(int tenantId, CancellationToken ct = default)
        => db.TenantApps
            .Where(a => a.TenantId == tenantId && a.RevokedAt == null)
            .Select(a => a.App)
            .OrderBy(a => a)
            .ToListAsync(ct);

    public Task<TenantApp?> FindGrantAsync(int tenantId, string app, CancellationToken ct = default)
        => db.TenantApps.FirstOrDefaultAsync(
            a => a.TenantId == tenantId && a.App == app && a.RevokedAt == null, ct);

    public void Add(TenantApp grant) => db.TenantApps.Add(grant);

    public void Audit(PlatformAuditLog entry) => db.AuditLogs.Add(entry);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public class EFIdempotencyService(PlatformDbContext db) : IIdempotencyService
{
    public Task<string?> FindResponseAsync(string operation, string key, CancellationToken ct = default)
        => db.IdempotencyRecords
            .Where(r => r.Operation == operation && r.Key == key)
            .Select(r => r.ResponseJson)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> TryRecordAsync(
        string operation, string key, string responseJson, CancellationToken ct = default)
    {
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
            // index is the guard and this is its expected outcome, not an error.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
