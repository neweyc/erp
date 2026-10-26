using AppPlatform.Tenancy;

namespace AppPlatform.Platform.Services;

/// <summary>
/// Where tenants sign in, from <c>Tenant:BaseUrl</c>: the same setting core reads, so the address
/// the console shows is the one core will recognize. An interface only because handlers depend on
/// interfaces; tests use the real <see cref="TenantSite"/>, which is plain configuration.
/// </summary>
public interface ITenantSite
{
    /// <summary>The shell's address with no tenant in it, such as <c>https://app.example.com</c>.</summary>
    Uri BaseUrl { get; }

    /// <summary><c>https://acme-fire.app.example.com</c> for the address <c>acme-fire</c>.</summary>
    string SignInUrl(string subdomain);
}

public sealed record TenantSite(Uri BaseUrl) : ITenantSite
{
    public string SignInUrl(string subdomain) => TenantAddress.SignInUrl(subdomain, BaseUrl);
}
