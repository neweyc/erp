using System.Text.Json;

namespace AppPlatform.Outbox;

/// <summary>
/// Writes each email to a file instead of sending it.
///
/// A controlled local substitute for an external service, used in development and by the browser
/// journey. It exercises the REAL delivery path — the same worker, claim, retry and dispatch — and
/// substitutes only the final hop, which is the one thing a test cannot own.
///
/// It is not a mock of the outbox. The alternative the browser journey used before this existed
/// was reading the invitation token straight out of the database, which proves nothing about
/// whether an invitation is ever delivered.
/// </summary>
public sealed class FileEmailTransport(string directory) : IOutboxTransport
{
    public string Transport => OutboxTransports.Email;

    public async Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            Directory.CreateDirectory(directory);

            // Named by message id, so a redelivery overwrites rather than accumulating — and so a
            // reader can correlate a captured email with the row that produced it.
            var path = Path.Combine(directory, $"{message.Id}.json");

            var captured = JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                to = message.Destination,
                capturedAt = DateTimeOffset.UtcNow,
                // The payload verbatim. A capture that reformatted it would let a payload bug
                // pass here and fail against a real transport.
                payload = JsonDocument.Parse(message.Payload).RootElement,
            });

            await File.WriteAllTextAsync(path, captured, ct);

            return DeliveryResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient: a full or unwritable disk is a condition that clears.
            return DeliveryResult.Transient($"{ex.GetType().Name}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            // A malformed payload will never become valid by waiting.
            return DeliveryResult.Fatal($"Payload is not valid JSON: {ex.Message}");
        }
    }
}
