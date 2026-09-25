namespace AppPlatform.Platform.Data;

/// <summary>
/// Makes an externally-triggered write safe to retry.
///
/// Provisioning is the case that needs it: a call that times out has an unknown outcome, and a
/// retry without this creates a SECOND tenant — a failure the caller cannot see and the
/// operator discovers as duplicate customers. The key is supplied by the caller, stored under a
/// unique index, and a replay returns the original result rather than doing the work again.
/// </summary>
public class IdempotencyRecord
{
    public long Id { get; set; }

    /// <summary>Caller-supplied. Unique across the whole table — these are operations, not rows.</summary>
    public required string Key { get; set; }

    /// <summary>
    /// Scopes the key to an operation, so the same key reused for a different call is a
    /// conflict rather than a silent replay of an unrelated result.
    /// </summary>
    public required string Operation { get; set; }

    /// <summary>The original response, replayed verbatim on retry.</summary>
    public required string ResponseJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
