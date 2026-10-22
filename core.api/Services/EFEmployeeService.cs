using AppPlatform.Core.Data;
using AppPlatform.Outbox;
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

    public Task<Employee?> FindByIdAsync(Guid id, CancellationToken ct = default)
        => db.Employees.FirstOrDefaultAsync(e => e.Id == id && !e.Deleted, ct);

    public Task<bool> EmailInUseAsync(string email, CancellationToken ct = default)
        => db.Employees.AnyAsync(e => e.Email == email && !e.Deleted, ct);

    public Task<bool> EmailInUseByAnotherAsync(string email, Guid employeeId, CancellationToken ct = default)
        => db.Employees.AnyAsync(e => e.Email == email && e.Id != employeeId && !e.Deleted, ct);

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

    public Task<List<Session>> ListLiveSessionsAsync(Guid userId, CancellationToken ct = default)
        => db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);

    public Task<List<UserToken>> ListInvitationTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        => db.UserTokens.Where(t => t.Purpose == TokenPurpose.Invite && userIds.Contains(t.UserId)).ToListAsync(ct);

    public Task<List<InvitationEmail>> ListInvitationEmailsAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken ct = default)
        // Only the status leaves the database: the payload holds the plaintext link.
        => db.Set<OutboxMessage>()
            .Where(m => messageIds.Contains(m.Id))
            .Select(m => new InvitationEmail(m.Id, m.Status))
            .ToListAsync(ct);

    public void AddToken(UserToken token) => db.UserTokens.Add(token);

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => db.Database.BeginTransactionAsync(ct);

    public async Task LockRoleChangesAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot lock role changes without a tenant scope.");

        // A transaction-scoped advisory lock, keyed by tenant: released at commit or rollback, and
        // a change in one tenant never waits for another tenant's. Same pattern as KeyRing. Raw
        // SQL because EF has no advisory locks; it reads and writes no rows, so the missing query
        // filter does not matter, and the tenant is bound into the key explicitly.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({RoleChangeLockKey(tenantId)}))", ct);
    }

    public async Task ShareRoleChangeLockAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot lock role changes without a tenant scope.");

        // The shared form of the lock above, on the same key.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared(hashtext({RoleChangeLockKey(tenantId)}))", ct);
    }

    /// <summary>Public so the real-PostgreSQL tests can hold the same lock the handler takes.</summary>
    public static string RoleChangeLockKey(int tenantId) => $"app-platform:role-change:{tenantId}";

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
