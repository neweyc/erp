using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppPlatform.Ids;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppPlatform.Audit;

/// <summary>
/// Turns pending changes to <see cref="IAuditable"/> entities into <see cref="AuditEntry"/> rows,
/// staged into the same save. Called by <see cref="AuditedDbContext"/>; feature code never calls it.
///
/// For an update or delete, the "before" is READ FROM THE DATABASE, with the row LOCKED, inside
/// the transaction the save then commits in. Both halves matter:
/// <list type="bullet">
/// <item>From the database, not EF's snapshot: the snapshot is only true for an entity this
/// context loaded. Attach a detached entity with Update() and its originals equal whatever the
/// caller supplied, so the history would show no change — or record a caller's claim as what was
/// deleted.</item>
/// <item>Locked: otherwise another writer can change the row between this read and the write,
/// and the history records a "before" that was not what got overwritten.</item>
/// </list>
/// The cost is one keyed lock-and-read per updated or deleted auditable entity.
/// </summary>
public static class AuditTrail
{
    public const string Redacted = "[redacted]";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Enums as their names, matching how the entity columns store them. A number would change
        // meaning the day someone reorders the enum.
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Change(object? Old, object? New);

    /// <summary>
    /// True when this save changes or removes an existing auditable row — the case that needs a
    /// transaction around the lock, the read and the write.
    /// </summary>
    public static bool TouchesExistingRows(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ChangeTracker.DetectChanges();
        return context.ChangeTracker.Entries<IAuditable>()
            .Any(e => e.State is EntityState.Modified or EntityState.Deleted);
    }

    /// <summary>
    /// Stages one audit row per changed auditable entity and returns them, so the caller can
    /// withdraw them if the save fails. Must run BEFORE the tenant guard, which is what stamps
    /// each new row with the tenant, and inside the save's transaction, so the locks hold.
    /// </summary>
    public static IReadOnlyList<AuditEntry> Stage(DbContext context, AuditActor? actor, DateTimeOffset now)
    {
        var pending = Pending(context);
        var stored = new Dictionary<EntityEntry, PropertyValues>();

        foreach (var entry in Existing(pending))
        {
            var visible = !context.Database.IsRelational()
                || LockQuery(context, entry).ToList().Count == 1;

            stored[entry] = Verified(entry, visible ? entry.GetDatabaseValues() : null);
        }

        return Add(context, Build(pending, stored, actor, now));
    }

    /// <inheritdoc cref="Stage"/>
    public static async Task<IReadOnlyList<AuditEntry>> StageAsync(
        DbContext context, AuditActor? actor, DateTimeOffset now, CancellationToken ct)
    {
        var pending = Pending(context);
        var stored = new Dictionary<EntityEntry, PropertyValues>();

        foreach (var entry in Existing(pending))
        {
            var visible = !context.Database.IsRelational()
                || (await LockQuery(context, entry).ToListAsync(ct)).Count == 1;

            stored[entry] = Verified(entry, visible ? await entry.GetDatabaseValuesAsync(ct) : null);
        }

        return Add(context, Build(pending, stored, actor, now));
    }

    /// <summary>
    /// The auditable entries this save will write. An edit to an audit row never reaches here:
    /// <see cref="AuditEntry"/> is <see cref="IAppendOnly"/>, refused by the guard that runs first.
    /// </summary>
    private static List<EntityEntry> Pending(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // SaveChanges would detect changes anyway, but only AFTER this runs — and a property set
        // since the last detection would otherwise be missing from the row.
        context.ChangeTracker.DetectChanges();

        // Materialised: rows are added to the tracker later, which would break an open enumeration.
        return [.. context.ChangeTracker.Entries().Where(e => e.Entity is IAuditable
            && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)];
    }

    /// <summary>
    /// The entries whose rows must be locked, in a FIXED order — table, then key. Two saves that
    /// touch the same rows then lock them in the same sequence, instead of in whatever order each
    /// happened to track them, which is how two otherwise-compatible saves deadlock.
    /// </summary>
    private static IEnumerable<EntityEntry> Existing(List<EntityEntry> pending)
        => pending
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .OrderBy(e => e.Metadata.GetSchemaQualifiedTableName(), StringComparer.Ordinal)
            .ThenBy(e => e.Property(e.Metadata.FindPrimaryKey()!.Properties.Single().Name).OriginalValue);

    /// <summary>
    /// <c>SELECT … FOR UPDATE</c> on the row this entry will write, scoped to its own tenant.
    ///
    /// Raw SQL, which applies no query filter — so the tenant is in the WHERE clause explicitly.
    /// That also means a forged write carrying another tenant's row id locks nothing: it finds no
    /// row and is refused, rather than holding a lock on a row it has no business touching.
    /// </summary>
    private static IQueryable<int> LockQuery(DbContext context, EntityEntry entry)
    {
        var entity = entry.Metadata;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var sql = context.GetService<ISqlGenerationHelper>();

        var key = entity.FindPrimaryKey()!.Properties.Single();
        var tenant = entity.FindProperty(nameof(ITenantScoped.TenantId))!;

        // Only IDENTIFIERS are concatenated, and they come from the EF model, never from input, and
        // are delimited by the provider. Both values are parameters ({0}, {1}).
        var lockSql =
            $"SELECT 1 AS \"Value\" FROM {sql.DelimitIdentifier(table.Name, table.Schema)} " +
            $"WHERE {sql.DelimitIdentifier(key.GetColumnName(table)!)} = {{0}} " +
            $"AND {sql.DelimitIdentifier(tenant.GetColumnName(table)!)} = {{1}} FOR UPDATE";

        return context.Database.SqlQueryRaw<int>(
            lockSql,
            entry.Property(key.Name).OriginalValue!,
            entry.Property(tenant.Name).OriginalValue!);
    }

    /// <summary>
    /// The stored row, or a refusal when there is no truthful "before" to record: the row is
    /// missing, or belongs to another tenant. The tenant is compared here as well as in the lock
    /// because EF's keyed read of database values does not apply the query filter — and the
    /// in-memory provider used by unit tests has no lock at all.
    ///
    /// Raised as a concurrency conflict, which is what it is from the caller's side — the row they
    /// meant to change is not there for them — and what handlers already turn into a 409.
    /// </summary>
    private static PropertyValues Verified(EntityEntry entry, PropertyValues? database)
    {
        var tenant = ((ITenantScoped)entry.Entity).TenantId;

        if (database is null || (int)database[nameof(ITenantScoped.TenantId)]! != tenant)
        {
            throw new DbUpdateConcurrencyException(
                $"Cannot change {entry.Metadata.ClrType.Name}: the row is not visible to this tenant. " +
                "It may have been deleted since it was read.");
        }

        return database;
    }

    /// <summary>
    /// Builds every row before adding any, so a refusal part-way through (no actor, a changed
    /// public id) leaves nothing half-staged in the tracker.
    /// </summary>
    private static List<AuditEntry> Build(
        List<EntityEntry> pending, Dictionary<EntityEntry, PropertyValues> stored,
        AuditActor? actor, DateTimeOffset now)
    {
        var rows = new List<AuditEntry>();

        foreach (var entry in pending)
        {
            var action = entry.State switch
            {
                EntityState.Added => AuditAction.Created,
                EntityState.Modified => AuditAction.Updated,
                _ => AuditAction.Deleted,
            };

            var database = action == AuditAction.Created ? null : stored[entry];
            var publicId = EntityIdOf(entry, database);
            var changes = ChangesOf(entry, action, database);

            // Marked modified, but nothing written differs from what is stored — a value set to
            // itself. No row: there is no change to attribute.
            if (changes.Count == 0) continue;

            if (actor is null)
            {
                throw new AuditActorMissingException(
                    $"Cannot save a change to {entry.Metadata.ClrType.Name} with no audit actor. " +
                    "In a request the actor is the caller; before a session exists, declare one " +
                    "through IAuditActorScope.");
            }

            rows.Add(new AuditEntry
            {
                OccurredAt = now,
                ActorKind = actor.Kind,
                ActorId = actor.PrincipalId,
                ActorName = actor.SystemName,
                Action = action,
                EntityType = entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name,
                EntityId = publicId,
                Changes = JsonSerializer.Serialize(changes, Json),
                // TenantId is left for the tenant guard to stamp, exactly as for any other insert.
            });
        }

        return rows;
    }

    /// <summary>
    /// The public id the row is filed under. For an existing row it is the STORED one: a detached
    /// delete that omitted or invented the public id would otherwise file the deletion under a
    /// name the entity never had. And it may not change — it is what integrations hold.
    /// </summary>
    private static string EntityIdOf(EntityEntry entry, PropertyValues? database)
    {
        var supplied = ((IPublicIdentified)entry.Entity).PublicId;
        if (database is null) return supplied;

        var stored = (string)database[nameof(IPublicIdentified.PublicId)]!;

        if (entry.State == EntityState.Modified && supplied != stored)
        {
            throw new InvalidOperationException(
                $"Cannot change the public id of {entry.Metadata.ClrType.Name} '{stored}': a public id " +
                "is assigned once and never reissued.");
        }

        return stored;
    }

    private static IReadOnlyList<AuditEntry> Add(DbContext context, List<AuditEntry> rows)
    {
        context.Set<AuditEntry>().AddRange(rows);
        return rows;
    }

    private static Dictionary<string, Change> ChangesOf(
        EntityEntry entry, AuditAction action, PropertyValues? database)
    {
        var changes = new Dictionary<string, Change>();

        foreach (var property in entry.Properties)
        {
            var metadata = property.Metadata;

            // The row's identity, not a change to it: the entity id is already on the audit row,
            // and the tenant is the audit row's own tenant.
            if (metadata.IsPrimaryKey()
                || metadata.Name is nameof(ITenantScoped.TenantId) or nameof(IPublicIdentified.PublicId))
            {
                continue;
            }

            // A placeholder EF replaces during the save — typically a foreign key to a row added
            // in the same save. Recording it would write a value that never existed.
            if (action != AuditAction.Deleted && property.IsTemporary)
            {
                throw new InvalidOperationException(
                    $"Cannot audit {entry.Metadata.ClrType.Name}.{metadata.Name}: its value is not " +
                    "known until the save completes. Save the entity it refers to first.");
            }

            var old = database?[metadata];
            var current = action == AuditAction.Deleted ? null : property.CurrentValue;

            // An update writes only modified columns, so only those can have changed.
            if (action == AuditAction.Updated && (!property.IsModified || Equals(old, current))) continue;

            // Nulls on create and delete are noise: "was nothing, is nothing" records no fact.
            if (action != AuditAction.Updated && old is null && current is null) continue;

            var redacted = metadata.PropertyInfo?.GetCustomAttribute<AuditRedactedAttribute>() is not null;

            changes[metadata.GetColumnName()] = redacted
                ? new Change(old is null ? null : Redacted, current is null ? null : Redacted)
                : new Change(old, current);
        }

        return changes;
    }
}
