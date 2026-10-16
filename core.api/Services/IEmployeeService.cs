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
    /// Locks the tenant's ACTIVE administrators (<c>FOR UPDATE</c>) until the current transaction
    /// ends, and returns their ids. A caller about to remove an administrator holds this while it
    /// checks that another remains. Role changes are already serialized by
    /// <see cref="LockRoleChangesAsync"/>; this row lock also stops any OTHER write to an
    /// administrator (a future deactivation, say) from landing between the check and the commit.
    /// Must run inside <see cref="BeginTransactionAsync"/>, after <see cref="LockRoleChangesAsync"/>.
    /// </summary>
    Task<List<Guid>> LockActiveAdminsAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes the tenant's role-change lock until the current transaction ends. Every role change
    /// takes it FIRST, before reading anything, so changes in one tenant run strictly one after
    /// another: each reads the admins and the target user only after the previous one committed.
    /// One lock per tenant, rather than row locks in some order, because the set of rows a change
    /// needs (the admins) can itself be changed by the change before it, and no row order can be
    /// agreed for rows a waiter has not seen yet. Must run inside <see cref="BeginTransactionAsync"/>.
    /// </summary>
    Task LockRoleChangesAsync(CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}
