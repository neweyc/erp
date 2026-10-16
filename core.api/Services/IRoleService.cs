using AppPlatform.Core.Data;

namespace AppPlatform.Core.Services;

/// <summary>
/// The tenant's custom roles. A thin EF wrapper, like the others. Writes happen inside
/// <see cref="IUserService.BeginTransactionAsync"/> after <see cref="IUserService.LockRoleChangesAsync"/>:
/// both services share the request's one DbContext, so they share its transaction.
/// </summary>
public interface IRoleService
{
    /// <summary>Every custom role in the tenant, ordered by name.</summary>
    Task<List<Role>> ListAsync(CancellationToken ct = default);

    Task<Role?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);

    void Add(Role role);
    void Remove(Role role);
}
