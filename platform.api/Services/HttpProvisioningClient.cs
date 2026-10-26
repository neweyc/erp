using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AppPlatform.Platform.Services;

public sealed class HttpProvisioningClient(HttpClient http, string apiKey) : IProvisioningClient
{
    public const string ApiKeyHeader = "X-Internal-Key";

    public async Task<ProvisionResult> ProvisionAsync(
        ProvisionRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/core/v1/internal/tenants")
        {
            Content = JsonContent.Create(new
            {
                name = request.TenantName,
                adminEmail = request.AdminEmail,
                idempotencyKey = request.IdempotencyKey,
                subdomain = request.Subdomain,
            }),
        };

        // The shared secret, never a cookie. This endpoint is reachable only from inside the
        // deployment; nginx blocks the internal prefix publicly, and an unset key makes it 404
        // rather than 401 — an endpoint that announces itself is an endpoint worth attacking.
        message.Headers.Add(ApiKeyHeader, apiKey);

        var response = await http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new ProvisionResult(
                false, null, $"core returned {(int)response.StatusCode}: {body}", RefusalOf(response.StatusCode, body));
        }

        using var json = JsonDocument.Parse(body);
        return new ProvisionResult(
            true, json.RootElement.GetProperty("tenantId").GetString(), null);
    }

    /// <summary>
    /// Core's refusal, when it refused the request rather than failed: a 400 or 409 carrying a
    /// problem code and message. Anything else (a 401 for a wrong internal key, a 500, a proxy's
    /// error page) is a failure for the operator to retry or report, not a refusal to act on.
    /// </summary>
    private static ProvisionRefusal? RefusalOf(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
            return null;

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("problemCode", out var code) && code.ValueKind == JsonValueKind.String
                && json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? new ProvisionRefusal(status == HttpStatusCode.Conflict, code.GetString()!, message.GetString()!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
