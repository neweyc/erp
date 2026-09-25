using System.Text.Json;
using AppPlatform.Api;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Tenants;

/// <summary>
/// Creates a tenant by asking core to do it.
///
/// Platform never writes tenant business data. The tenant row, its company, and the admin
/// invite must commit in ONE transaction and a transaction cannot span two services, so core
/// performs the whole thing and platform holds the commercial record.
/// </summary>
public static class CreateTenantFeature
{
    public const string Operation = "create_tenant";

    public record CreateTenantCommand(string? Name, string? AdminEmail, string? IdempotencyKey);

    public class CreateTenantCommandHandler(
        IProvisioningClient provisioning,
        IIdempotencyService idempotency)
    {
        public async Task<CommandResult> Handle(CreateTenantCommand cmd, CancellationToken ct = default)
        {
            var name = cmd.Name?.Trim();
            var email = cmd.AdminEmail?.Trim().ToLowerInvariant();
            var key = cmd.IdempotencyKey?.Trim();

            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Invalid(PlatformProblems.ValidationFailed, "A tenant name is required.");

            if (string.IsNullOrWhiteSpace(email))
                return CommandResult.Invalid(PlatformProblems.ValidationFailed, "An admin email is required.");

            // Required, not optional. Provisioning is expensive and externally triggered: a
            // call that times out has an unknown outcome, and a retry without a key creates a
            // SECOND tenant — a failure the caller cannot see and the operator discovers as
            // duplicate customers.
            if (string.IsNullOrWhiteSpace(key))
            {
                return CommandResult.Invalid(
                    PlatformProblems.IdempotencyKeyRequired,
                    "An idempotency key is required so a retried request cannot provision twice.");
            }

            // Replay before doing anything. The original response is returned verbatim, so a
            // retry is indistinguishable from the first call.
            if (await idempotency.FindResponseAsync(Operation, key, ct) is { } existing)
                return CommandResult.Ok(JsonSerializer.Deserialize<JsonElement>(existing));

            var result = await provisioning.ProvisionAsync(new(name, email, key), ct);

            if (!result.Succeeded)
            {
                // Deliberately NOT recorded. Recording a failure would make every retry replay
                // the failure forever, when a retry after a transient fault is exactly what
                // should be allowed to succeed.
                return CommandResult.Invalid(PlatformProblems.ProvisioningFailed, result.Error ?? "Provisioning failed.");
            }

            var response = JsonSerializer.Serialize(new { tenantId = result.TenantPublicId });

            if (!await idempotency.TryRecordAsync(Operation, key, response, ct))
            {
                // Lost the race to a concurrent retry. The other caller's response is the
                // authoritative one — returning ours would hand out a different tenant id for
                // the same logical operation.
                var winner = await idempotency.FindResponseAsync(Operation, key, ct);
                if (winner is not null)
                    return CommandResult.Ok(JsonSerializer.Deserialize<JsonElement>(winner));
            }

            return CommandResult.Ok(JsonSerializer.Deserialize<JsonElement>(response));
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/platform/v1/tenants",
            async (
                CreateTenantCommand cmd,
                [FromServices] IProvisioningClient provisioning,
                [FromServices] IIdempotencyService idempotency,
                CancellationToken ct) =>
            {
                var handler = new CreateTenantCommandHandler(provisioning, idempotency);
                return (await handler.Handle(cmd, ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}

public static class PlatformProblems
{
    public const string ValidationFailed = "validation_failed";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string ProvisioningFailed = "provisioning_failed";
    public const string NotFound = "not_found";
    public const string AlreadyInThatState = "already_in_that_state";
    public const string TenantRetired = "tenant_retired";
    public const string AppAlreadyLicensed = "app_already_licensed";
}
