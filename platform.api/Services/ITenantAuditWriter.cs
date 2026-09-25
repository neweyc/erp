using AppPlatform.Platform.Data;

namespace AppPlatform.Platform.Features.Auth;

/// <summary>
/// Stages operator audit entries and flushes them.
///
/// Separate from <c>ITenantService</c> because sign-in must be able to audit a FAILURE, where
/// there is no operator, no session, and nothing else to save — and because a failed sign-in
/// that could not be recorded is the one case where losing the trail matters most.
/// </summary>
public interface ITenantAuditWriter
{
    void Write(PlatformAuditLog entry);
    Task FlushAsync(CancellationToken ct = default);
}
