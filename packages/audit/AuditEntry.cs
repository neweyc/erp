using AppPlatform.Tenancy;

namespace AppPlatform.Audit;

public enum AuditAction
{
    Created,
    Updated,
    Deleted,
}

/// <summary>
/// One change to one auditable entity. Append-only: runtime roles hold no UPDATE or DELETE on
/// the table, and the append-only guard refuses to save a modified or deleted entry.
/// </summary>
public class AuditEntry : ITenantScoped, IAppendOnly
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public AuditActorKind ActorKind { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>The table name, e.g. "employee".</summary>
    public required string EntityType { get; set; }

    /// <summary>The entity's public id.</summary>
    public required string EntityId { get; set; }

    /// <summary>JSON: <c>{ "column": { "old": …, "new": … } }</c>.</summary>
    public required string Changes { get; set; }
}
