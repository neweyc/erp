using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AppPlatform.Platform.Data;

/// <summary>Design-time only. See core's equivalent for why Program is not booted here.</summary>
public class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=design-time;Database=appplatform", npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options);
}
