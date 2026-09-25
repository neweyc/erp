using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Audit;

/// <summary>
/// Base for every context that holds an <see cref="IAuditable"/> entity. Maps the service's own
/// <c>audit_log</c> and stages audit rows on every save, so no derived context has to remember to.
/// <c>BoundaryTests</c> fails the build if a context with an auditable entity does not derive
/// from this.
/// </summary>
public abstract class AuditedDbContext(
    DbContextOptions options,
    ITenantProvider tenantProvider,
    IAuditActor auditActor,
    TimeProvider clock)
    : TenantedDbContext(options, tenantProvider)
{
    /// <summary>The schema this service's audit_log lives in — its own, never another's.</summary>
    protected abstract string AuditSchema { get; }

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Mapped BEFORE the base applies tenant filters, so the audit log gets one like every
        // other tenant-scoped table: tenant B cannot read tenant A's history.
        modelBuilder.AddAuditLog(AuditSchema);
        base.OnModelCreating(modelBuilder);
    }

    // Five rules shape both save methods below.
    //
    // 1. The append-only and tenant guards run FIRST, before any row is locked or read: a context
    //    in one tenant must not lock another tenant's row even briefly, which a caller-owned
    //    transaction would hold until it ends — and an edit to a posted row is refused before
    //    audit reads anything to describe it.
    //
    // 2. Staged before calling base, because base runs the tenant guard again — which is what
    //    stamps the new audit rows with the tenant. Staged after, they would reach the database
    //    with no tenant.
    //
    // 3. When an existing row is changed or removed, the lock, the read of its "before" values and
    //    the write share ONE transaction — this method's own when the caller has none (provisioning
    //    brings its own, and then the locks simply live in that). Without it the lock would be
    //    released before the write, and another writer could slip in between.
    //
    // 4. In a transaction of its own, changes are ACCEPTED only after the commit. EF otherwise marks
    //    them saved as the statements succeed; a commit that then fails rolls the database back
    //    while the tracker believes the work is done, and a retry on the same context would
    //    silently write nothing.
    //
    // 5. Staged rows are withdrawn if the save fails. Otherwise they stay in the tracker: a caller
    //    that corrects the problem and saves again would commit a row for a change that never
    //    happened alongside the real one, and a caller that discards the change would commit its
    //    audit row on its own.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AppendOnlyGuard.Enforce(ChangeTracker);
        TenantGuard.Enforce(ChangeTracker, CurrentTenantId);
        using var owned = NeedsOwnTransaction() ? Database.BeginTransaction() : null;

        var staged = AuditTrail.Stage(this, auditActor.Current, clock.GetUtcNow());
        try
        {
            var written = base.SaveChanges(acceptAllChangesOnSuccess && owned is null);
            if (owned is not null)
            {
                owned.Commit();
                if (acceptAllChangesOnSuccess) ChangeTracker.AcceptAllChanges();
            }
            return written;
        }
        catch
        {
            Withdraw(staged);
            throw;
        }
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AppendOnlyGuard.Enforce(ChangeTracker);
        TenantGuard.Enforce(ChangeTracker, CurrentTenantId);
        await using var owned = NeedsOwnTransaction()
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;

        var staged = await AuditTrail.StageAsync(this, auditActor.Current, clock.GetUtcNow(), cancellationToken);
        try
        {
            var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess && owned is null, cancellationToken);
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
                if (acceptAllChangesOnSuccess) ChangeTracker.AcceptAllChanges();
            }
            return written;
        }
        catch
        {
            Withdraw(staged);
            throw;
        }
    }

    /// <summary>
    /// A transaction is needed only to hold row locks from the audit read through the write. An
    /// insert-only save has nothing to lock, and the in-memory test provider has no transactions.
    /// </summary>
    private bool NeedsOwnTransaction()
        => Database.IsRelational()
            && Database.CurrentTransaction is null
            && AuditTrail.TouchesExistingRows(this);

    private void Withdraw(IEnumerable<AuditEntry> staged)
    {
        foreach (var row in staged) Entry(row).State = EntityState.Detached;
    }
}

public static class AuditModelExtensions
{
    /// <summary>
    /// One audit_log per owning service, in its own schema — for the same reason there is one
    /// outbox per schema: a shared table would make every save a cross-schema write.
    /// </summary>
    public static void AddAuditLog(this ModelBuilder modelBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log", schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.ActorKind).HasColumnName("actor_kind").HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.ActorName).HasColumnName("actor_name").HasMaxLength(100);
            e.Property(x => x.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(100);
            e.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(40);
            e.Property(x => x.Changes).HasColumnName("changes").HasColumnType("jsonb");

            // "The history of this record" — the read an audit log exists for.
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
        });
    }
}
