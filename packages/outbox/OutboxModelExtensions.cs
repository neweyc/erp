using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Outbox;

public static class OutboxModelExtensions
{
    /// <summary>
    /// Maps the outbox into the CALLER's own schema.
    ///
    /// One outbox per owning schema, never a shared table: a single outbox would make every
    /// save a cross-schema write, breaking the boundary with the very mechanism meant to
    /// respect it. Each service runs its own worker over its own rows.
    /// </summary>
    public static void AddOutbox(this ModelBuilder modelBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        modelBuilder.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox_event", schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.AggregateType).HasColumnName("aggregate_type").HasMaxLength(50);
            e.Property(x => x.AggregatePublicId).HasColumnName("aggregate_public_id").HasMaxLength(40);
            e.Property(x => x.AggregateVersion).HasColumnName("aggregate_version");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");

            e.HasIndex(x => x.TenantId);

            // Serves gap detection for a consumer reading one aggregate's history, and it is
            // UNIQUE: two events claiming the same aggregate version would make the sequence
            // meaningless in exactly the way it exists to prevent.
            e.HasIndex(x => new { x.TenantId, x.AggregateType, x.AggregatePublicId, x.AggregateVersion })
                .IsUnique();
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_message", schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.Transport).HasColumnName("transport").HasMaxLength(20);
            e.Property(x => x.Destination).HasColumnName("destination").HasMaxLength(320);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1000);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasIndex(x => x.TenantId);

            // The worker's claim query, and the only index it needs. Filtered so the table's
            // bulk — succeeded rows awaiting pruning — stays out of the hot path entirely.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                .HasFilter("status = 'Pending'");

            // Composite so a delivery cannot reference another tenant's event. Within one
            // schema the database can enforce this, and here it does.
            e.HasOne<OutboxEvent>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EventId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // The principal side of that composite key needs to be declared unique.
        modelBuilder.Entity<OutboxEvent>()
            .HasAlternateKey(x => new { x.TenantId, x.Id });
    }
}
