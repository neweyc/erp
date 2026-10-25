using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform.Services;

/// <summary>
/// One operator audit entry as the console shows it. The operator is named by email, since that is
/// how operators know each other; it is null for a failed sign-in, which has no operator yet.
/// </summary>
public record AuditRow(
    long Id, string Action, string? OperatorEmail, string? TenantId, string? Detail, DateTimeOffset At);

public interface IAuditLogService
{
    /// <summary>Newest first, ties broken by id. Only this tenant's entries when one is given.</summary>
    Task<List<AuditRow>> RecentAsync(string? tenantPublicId, int limit, CancellationToken ct = default);
}

public class EFAuditLogService(PlatformDbContext db) : IAuditLogService
{
    public Task<List<AuditRow>> RecentAsync(string? tenantPublicId, int limit, CancellationToken ct = default)
        => db.AuditLogs
            .Where(a => tenantPublicId == null || a.TenantPublicId == tenantPublicId)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Take(limit)
            .Select(a => new AuditRow(
                a.Id, a.Action,
                db.PlatformUsers.Where(u => u.Id == a.PlatformUserId).Select(u => u.Email).FirstOrDefault(),
                a.TenantPublicId, a.Detail, a.CreatedAt))
            .ToListAsync(ct);
}
