namespace AppPlatform.Outbox;

/// <summary>The outcome of one delivery attempt.</summary>
public sealed record DeliveryResult(bool Succeeded, string? Error = null, bool Permanent = false)
{
    public static readonly DeliveryResult Success = new(true);

    /// <summary>Worth retrying — a timeout, a 5xx, a rate limit.</summary>
    public static DeliveryResult Transient(string error) => new(false, error);

    /// <summary>
    /// Not worth retrying — a malformed address, a 400, an endpoint that refuses the payload.
    /// Dead-lettered immediately rather than consuming the whole backoff schedule to reach
    /// the same conclusion.
    /// </summary>
    public static DeliveryResult Fatal(string error) => new(false, error, Permanent: true);
}

/// <summary>
/// Sends one message. Implementations must never throw for an ordinary delivery failure —
/// return a result instead, so the worker records the reason rather than losing it to an
/// exception handler.
/// </summary>
public interface IOutboxTransport
{
    /// <summary>Which <see cref="OutboxTransports"/> value this handles.</summary>
    string Transport { get; }

    Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken cancellationToken);
}
