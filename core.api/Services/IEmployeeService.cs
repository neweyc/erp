using AppPlatform.Core.Data;
using AppPlatform.Outbox;
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
    Task<Employee?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> EmailInUseAsync(string email, CancellationToken ct = default);

    /// <summary>Whether an employee OTHER than this one has the address: for an edit, which may keep its own.</summary>
    Task<bool> EmailInUseByAnotherAsync(string email, Guid employeeId, CancellationToken ct = default);

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

    /// <summary>The user's sessions that have not been revoked, for deactivation to revoke.</summary>
    Task<List<Session>> ListLiveSessionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every invitation token issued to these users, used or not.</summary>
    Task<List<UserToken>> ListInvitationTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// The delivery status of these outbox messages, for those the outbox still keeps. Never the
    /// payload: it holds the plaintext link.
    /// </summary>
    Task<List<InvitationEmail>> ListInvitationEmailsAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken ct = default);

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
    /// can take access away (deactivation, termination) must take it too, and so does accepting an
    /// invitation, which must see a deactivation that commits first. Must run inside
    /// <see cref="BeginTransactionAsync"/>. docs/permissions.md §6.
    /// </summary>
    Task LockRoleChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes the same lock in SHARED mode until the current transaction ends: any number of
    /// sign-ins hold it at once, but none while an access change holds it, and an access change
    /// waits for the sign-ins already holding it. Sign-in takes it before reading the account and
    /// keeps it until its new session is committed, so a deactivation either sees that session and
    /// revokes it, or commits first and the sign-in is refused. Without it, a session created
    /// mid-deactivation would escape revocation and work again after reactivation.
    /// </summary>
    Task ShareRoleChangeLockAsync(CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}

/// <summary>One invitation email's delivery status.</summary>
public record InvitationEmail(Guid MessageId, OutboxStatus Status);
