using AppPlatform.Tenancy;

namespace AppPlatform.Platform.Services;

/// <summary>
/// Where tenants sign in, from <c>Tenant:BaseUrl</c>: the same setting core reads, so the address
/// the console shows is the one core will recognize. Configuration, not a service.
/// </summary>
public sealed record TenantSite(Uri BaseUrl)
{
    /// <summary><c>https://acme-fire.app.example.com</c> for the address <c>acme-fire</c>.</summary>
    public string SignInUrl(string subdomain) => TenantAddress.SignInUrl(subdomain, BaseUrl);
}
