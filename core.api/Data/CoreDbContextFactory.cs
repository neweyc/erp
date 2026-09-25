using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AppPlatform.Core.Data;

/// <summary>
/// Used by <c>dotnet ef</c> only.
///
/// Exists so the design-time tools never boot <c>Program</c>, which deliberately refuses to
/// start without a real connection string. The alternative — relaxing that check so migrations
/// can be generated — would let the service itself start misconfigured, which is the failure
/// worth keeping loud.
///
/// The connection string here is never opened: building a migration needs the provider's SQL
/// generator, not a server.
/// </summary>
public class CoreDbContextFactory : IDesignTimeDbContextFactory<CoreDbContext>
{
    public CoreDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql("Host=design-time;Database=appplatform", npgsql => npgsql
                // Must match Program: the history table name is baked into the generated
                // script, so a mismatch makes every migration look unapplied.
                .MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema))
            // Must match Program exactly: the migration is generated from THIS model, so a
            // convention applied in only one place produces a schema the app cannot read.
            .UseSnakeCaseNamingConvention()
            .Options;

        // No ambient tenant at design time. The query filters are still applied to the model —
        // which is what the migration is generated from — they simply resolve to nothing.
        return new CoreDbContext(options, new AmbientTenantProvider());
    }
}
