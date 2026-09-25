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
            }),
        };

        // The shared secret, never a cookie. This endpoint is reachable only from inside the
        // deployment; nginx blocks the internal prefix publicly, and an unset key makes it 404
        // rather than 401 — an endpoint that announces itself is an endpoint worth attacking.
        message.Headers.Add(ApiKeyHeader, apiKey);

        var response = await http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return new ProvisionResult(false, null, $"core returned {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        return new ProvisionResult(
            true, json.RootElement.GetProperty("tenantId").GetString(), null);
    }
}
