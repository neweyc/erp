namespace AppPlatform.Auth;

public enum PrincipalKind
{
    User,
    ApiKey,
}

/// <summary>
/// Who is making this request. Every handler takes one of these, never a bare user id.
///
/// EMS passed <c>Guid userId</c> because a user was the only thing that could call
/// anything. An API key is a principal here, and a signature that can only carry a user id
/// forces machine-authenticated calls to supply a fabricated one — which then lands in the
/// audit trail as a person who did not do it.
/// </summary>
public sealed record Caller
{
    public required Guid PrincipalId { get; init; }
    public required PrincipalKind Kind { get; init; }
    public required int TenantId { get; init; }
    public required int CompanyId { get; init; }
    public required string Role { get; init; }

    /// <summary>Null for an API key. Read it through <see cref="RequireUserId"/>.</summary>
    public Guid? UserId { get; init; }

    /// <summary>The core employee this principal acts as, when there is one. Gates self-service.</summary>
    public Guid? EmployeeId { get; init; }

    /// <summary>Apps the tenant has licensed. Empty is legitimate — a tenant may license none.</summary>
    public IReadOnlyList<string> LicensedApps { get; init; } = [];

    /// <summary>Granted scopes for an API key; empty for an interactive user.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>
    /// True when the tenant is suspended. The principal is still valid — authentication
    /// succeeded — and route authorization decides what remains reachable. Rejecting a
    /// suspended tenant at authentication would discard the identity that the promised
    /// data export needs.
    /// </summary>
    public bool TenantSuspended { get; init; }

    /// <summary>
    /// The acting user, for anything only a person may do. Fails loudly rather than
    /// returning <see cref="Guid.Empty"/>, which would be written to a record as though a
    /// user with that id had acted.
    /// </summary>
    public Guid RequireUserId() => UserId
        ?? throw new InvalidOperationException(
            $"This operation requires an interactive user; the caller is a {Kind}.");
}
