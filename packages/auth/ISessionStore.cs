namespace AppPlatform.Auth;

/// <summary>
/// Reads and touches session state. An interface so the rules in
/// <see cref="SessionEvaluator"/> can be tested without a database, and so the only code
/// that calls <c>identity_v1.session_context</c> is one implementation rather than
/// scattered queries.
/// </summary>
public interface ISessionStore
{
    /// <summary>Null when the id matches nothing. Never throws for a missing session.</summary>
    Task<SessionContext?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort. A failed touch must not fail the request: the cost is a session that
    /// times out slightly early, which is strictly better than a request that fails
    /// because bookkeeping did.
    /// </summary>
    Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
