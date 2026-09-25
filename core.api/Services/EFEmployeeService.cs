using AppPlatform.Core.Data;
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

public class EFUserService(CoreDbContext db) : IUserService
{
    public Task<bool> EmailInUseAsync(string email, CancellationToken ct = default)
        => db.Users.AnyAsync(u => u.Email == email, ct);

    public Task<User?> FindByEmployeeAsync(Guid employeeId, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.EmployeeId == employeeId, ct);

    public void Add(User user) => db.Users.Add(user);
}
