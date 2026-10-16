using AppPlatform.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core.Services;

public class EFRoleService(CoreDbContext db) : IRoleService
{
    public Task<List<Role>> ListAsync(CancellationToken ct = default)
        => db.Roles.OrderBy(r => r.Name).ToListAsync(ct);

    public Task<Role?> FindByPublicIdAsync(string publicId, CancellationToken ct = default)
        => db.Roles.FirstOrDefaultAsync(r => r.PublicId == publicId, ct);

    public void Add(Role role) => db.Roles.Add(role);

    public void Remove(Role role) => db.Roles.Remove(role);
}
