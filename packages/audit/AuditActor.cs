namespace AppPlatform.Audit;

public enum AuditActorKind
{
    User,
    ApiKey,
    System,
}

/// <summary>
/// Who an audit row attributes a change to. A principal — a user or an API key — or, before any
/// session exists, a named system process. Never a user id alone: an integration key's writes
/// must read as the key, not as a person who did not make them.
/// </summary>
public sealed record AuditActor
{
    public AuditActorKind Kind { get; }

    /// <summary>The user id or API key id. Null for <see cref="AuditActorKind.System"/>.</summary>
    public Guid? PrincipalId { get; }

    /// <summary>For <see cref="AuditActorKind.System"/> only: which process, e.g. "provisioning".</summary>
    public string? SystemName { get; }

    private AuditActor(AuditActorKind kind, Guid? principalId, string? systemName)
        => (Kind, PrincipalId, SystemName) = (kind, principalId, systemName);

    public static AuditActor User(Guid userId) => new(AuditActorKind.User, RequireId(userId), null);

    public static AuditActor ApiKey(Guid keyId) => new(AuditActorKind.ApiKey, RequireId(keyId), null);

    public static AuditActor System(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(AuditActorKind.System, null, name);
    }

    /// <summary>
    /// Guid.Empty is refused for the same reason <c>Caller.RequireUserId</c> refuses it: stored, it
    /// reads as though a principal with that id had acted.
    /// </summary>
    private static Guid RequireId(Guid id) => id == Guid.Empty
        ? throw new ArgumentException("An audit actor needs a real principal id, not Guid.Empty.", nameof(id))
        : id;
}

/// <summary>The actor for the current DI scope, or null when nothing has established one.</summary>
public interface IAuditActor
{
    AuditActor? Current { get; }
}

/// <summary>
/// Declares the actor for work that happens before any session exists — accepting an invitation,
/// provisioning, a seed. The audit counterpart of <c>IBackgroundTenantScope</c>, and like it,
/// never resolved from ordinary feature code: in a request the actor IS the caller.
/// </summary>
public interface IAuditActorScope
{
    void UseActor(AuditActor actor);
}

/// <summary>A plain settable actor for CLI commands, seeds and tests. One per DI scope.</summary>
public sealed class AmbientAuditActor : IAuditActor, IAuditActorScope
{
    public AuditActor? Current { get; private set; }

    public AmbientAuditActor() { }

    public AmbientAuditActor(AuditActor actor) => Current = actor;

    public void UseActor(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Current = actor;
    }
}

/// <summary>
/// An auditable change was saved with no actor. Thrown rather than recorded as "unknown": a row
/// nobody can be held to is worse than a failed save, which is at least visible and retryable.
/// </summary>
public sealed class AuditActorMissingException(string message) : InvalidOperationException(message);
