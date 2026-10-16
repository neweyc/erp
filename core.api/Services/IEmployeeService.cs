using AppPlatform.Core.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppPlatform.Core.Services;

/// <summary>
/// A thin EF wrapper. Handlers depend on this interface, never on the DbContext, so a handler's
/// rules are unit-testable without a database — which is where most of a feature's behaviour
/// actually lives.
/// </summary>
public interface IEmployeeService
{
    Task<List<Employee>> ListAsync(bool includeTerminated, CancellationToken ct = default);
    Task<Employee?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);
    Task<bool> EmailInUseAsync(string email, CancellationToken ct = default);
    Task<int> DefaultCompanyIdAsync(CancellationToken ct = default);
    void Add(Employee employee);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IUserService
{
    Task<bool> EmailInUseAsync(string email, CancellationToken ct = default);
    Task<User?> FindByEmployeeAsync(Guid employeeId, CancellationToken ct = default);
    Task<User?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);

    /// <summary>Every account in the tenant, deactivated included, ordered by email.</summary>
    Task<List<User>> ListAsync(CancellationToken ct = default);

    void Add(User user);

    /// <summary>An invitation or reset token for a user, saved with the next save.</summary>
    void AddToken(UserToken token);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes the tenant's role-change lock until the current transaction ends. EVERY role write
    /// (assigning, creating, editing, deleting a role) takes it FIRST, before reading anything, so
    /// role writes in one tenant run strictly one after another and each reads what the previous
    /// one committed. One lock per tenant, rather than row locks in some order, because the rows a
    /// check depends on (who can manage users) are changed by the very writes being serialized,
    /// and no row order can be agreed for rows a waiter has not seen yet. Any future write that
    /// can take access away (deactivation, termination) must take it too. Must run inside
    /// <see cref="BeginTransactionAsync"/>. docs/permissions.md §6.
    /// </summary>
    Task LockRoleChangesAsync(CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}
