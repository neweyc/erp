namespace AppPlatform.Tenancy;

/// <summary>
/// Thrown when a save would cross a tenant boundary. Its own type rather than
/// InvalidOperationException so an exception handler can tell a genuine isolation
/// failure — which is a security event and should be logged as one — from the ordinary
/// invalid-state exceptions that share that base type.
/// </summary>
public sealed class TenantScopeViolationException(string message) : InvalidOperationException(message);
