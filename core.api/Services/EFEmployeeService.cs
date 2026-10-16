using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core.Services;

public class EFEmployeeService(CoreDbContext db) : IEmployeeService
{
    public Task<List<Employee>> ListAsync(bool includeTerminated, CancellationToken ct = default)
        => db.Employees
            .Where(e => !e.Deleted)
            // Terminated employees are excluded by DEFAULT and included on request. They are
            // not deleted, so anything historical still resolves them; they simply do not
            // belong in a roster of who works here.
            .Where(e => includeTerminated || e.Status != EmployeeStatus.Terminated)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync(ct);

    // FirstOrDefaultAsync, never FindAsync: Find can be served from the change tracker and so
    // bypass the tenant query filter entirely.
    public Task<Employee?> FindByPublicIdAsync(string publicId, CancellationToken ct = default)
        => db.Employees.FirstOrDefaultAsync(e => e.PublicId == publicId && !e.Deleted, ct);

    public Task<bool> EmailInUseAsync(string email, CancellationToken ct = default)
        => db.Employees.AnyAsync(e => e.Email == email && !e.Deleted, ct);

    public async Task<int> DefaultCompanyIdAsync(CancellationToken ct = default)
    {
        // Exactly one company per tenant today. Ordered so that the day a tenant has several,
        // this returns the same one every time instead of whatever the plan happened to yield.
        var id = await db.Companies
            .Where(c => c.Active)
            .OrderBy(c => c.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(ct);

        return id ?? throw new InvalidOperationException(
            "This tenant has no company. Provisioning must create one; an employee cannot " +
            "exist without a legal entity to belong to.");
    }

    public void Add(Employee employee) => db.Employees.Add(employee);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public class EFUserService(CoreDbContext db, ITenantProvider tenant) : IUserService
{
    public Task<bool> EmailInUseAsync(string email, CancellationToken ct = default)
        => db.Users.AnyAsync(u => u.Email == email, ct);

    public Task<User?> FindByEmployeeAsync(Guid employeeId, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.EmployeeId == employeeId, ct);

    public Task<User?> FindByPublicIdAsync(string publicId, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.PublicId == publicId, ct);

    public Task<List<User>> ListAsync(CancellationToken ct = default)
        => db.Users.OrderBy(u => u.Email).ToListAsync(ct);

    public void Add(User user) => db.Users.Add(user);

    public void AddToken(UserToken token) => db.UserTokens.Add(token);

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => db.Database.BeginTransactionAsync(ct);

    public async Task<List<Guid>> LockActiveAdminsAsync(CancellationToken ct = default)
    {
        // Raw SQL because EF has no FOR UPDATE, and raw SQL applies no query filter — so the tenant
        // is bound explicitly, from the same ambient scope the filter uses.
        //
        // Ordered by id so that callers take the row locks in a consistent order. Role changes
        // never contend here, because they already hold the tenant's role-change lock.
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot lock administrators without a tenant scope.");

        return await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM identity."user"
            WHERE tenant_id = {tenantId} AND role = {RoleCatalogue.Admin} AND status = 'Active'
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(ct);
    }

    public async Task LockRoleChangesAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot lock role changes without a tenant scope.");

        // A transaction-scoped advisory lock, keyed by tenant: released at commit or rollback, and
        // a change in one tenant never waits for another tenant's. Same pattern as KeyRing.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({RoleChangeLockKey(tenantId)}))", ct);
    }

    /// <summary>Public so the real-PostgreSQL tests can hold the same lock the handler takes.</summary>
    public static string RoleChangeLockKey(int tenantId) => $"app-platform:role-change:{tenantId}";

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
