using AppPlatform.Core.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppPlatform.Core.Services;

public class EFCatalogService(CoreDbContext db, ITenantProvider tenant) : ICatalogService
{
    public Task<List<Department>> ListDepartmentsAsync(CancellationToken ct = default)
        => db.Departments.OrderBy(d => d.Name).ToListAsync(ct);

    public Task<List<JobTitle>> ListJobTitlesAsync(CancellationToken ct = default)
        => db.JobTitles.OrderBy(t => t.Name).ToListAsync(ct);

    public Task<List<Location>> ListLocationsAsync(CancellationToken ct = default)
        => db.Locations.OrderBy(l => l.Name).ToListAsync(ct);

    // FirstOrDefaultAsync, never FindAsync: Find can be served from the change tracker and so
    // bypass the tenant query filter.
    public Task<Department?> FindDepartmentAsync(string publicId, CancellationToken ct = default)
        => db.Departments.FirstOrDefaultAsync(d => d.PublicId == publicId, ct);

    public Task<JobTitle?> FindJobTitleAsync(string publicId, CancellationToken ct = default)
        => db.JobTitles.FirstOrDefaultAsync(t => t.PublicId == publicId, ct);

    public Task<Location?> FindLocationAsync(string publicId, CancellationToken ct = default)
        => db.Locations.FirstOrDefaultAsync(l => l.PublicId == publicId, ct);

    public void Add(Department department) => db.Departments.Add(department);
    public void Add(JobTitle jobTitle) => db.JobTitles.Add(jobTitle);
    public void Add(Location location) => db.Locations.Add(location);

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => db.Database.BeginTransactionAsync(ct);

    public async Task LockCatalogsAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot lock the catalogs without a tenant scope.");

        // The same pattern as the role-change lock (EFUserService): transaction-scoped, keyed by
        // tenant, raw SQL that reads and writes no rows, with the tenant bound into the key.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({CatalogLockKey(tenantId)}))", ct);
    }

    /// <summary>Public so the real-PostgreSQL tests can hold the same lock the handlers take.</summary>
    public static string CatalogLockKey(int tenantId) => $"app-platform:catalog:{tenantId}";

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
