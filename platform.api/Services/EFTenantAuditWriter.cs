using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Auth;

namespace AppPlatform.Platform.Services;

public sealed class EFTenantAuditWriter(PlatformDbContext db) : ITenantAuditWriter
{
    public void Write(PlatformAuditLog entry) => db.AuditLogs.Add(entry);

    public Task FlushAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
