using AppPlatform.Tenancy;
using Xunit;

namespace AppPlatform.Tenancy.Tests;

public class TenantAddressTests
{
    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("  Acme-Fire ", "acme-fire")]
    [InlineData("station7", "station7")]
    [InlineData("a1b", "a1b")]
    public void Accepts_a_well_formed_address_and_stores_it_lowercase(string input, string expected)
    {
        Assert.True(TenantAddress.TryNormalize(input, out var subdomain, out var problem));
        Assert.Null(problem);
        Assert.Equal(expected, subdomain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a2345678901234567890123456789012345678901")]  // 41 characters
    [InlineData("7station")]   // starts with a digit
    [InlineData("acme-")]      // ends with a hyphen
    [InlineData("-acme")]
    [InlineData("xn--acme")]   // double hyphen: DNS's encoded-name prefix
    [InlineData("acme.fire")]  // a dot would make it two labels
    [InlineData("acme_fire")]
    [InlineData("acmé")]
    [InlineData("www")]        // reserved
    [InlineData("Console")]    // reserved, whatever the case
    public void Refuses_an_address_that_breaks_the_rules(string? input)
    {
        Assert.False(TenantAddress.TryNormalize(input, out var subdomain, out var problem));
        Assert.Null(subdomain);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("acme.app.example.com", "acme")]
    [InlineData("ACME.App.Example.com", "acme")]
    [InlineData("acme.app.example.com.", "acme")]   // a fully qualified name's trailing dot
    [InlineData("acme.localhost", "acme")]
    public void Reads_the_subdomain_from_a_tenant_host(string host, string expected)
    {
        var baseDomain = host.EndsWith("localhost") ? "localhost" : "app.example.com";

        Assert.Equal(expected, TenantAddress.SubdomainOf(host, baseDomain));
    }

    [Theory]
    [InlineData("app.example.com")]                 // the bare base domain
    [InlineData("acme.other.com")]                  // another domain altogether
    [InlineData("acmeapp.example.com")]             // shares the suffix's letters, not its labels
    [InlineData("a.acme.app.example.com")]          // two labels deep
    [InlineData("www.app.example.com")]             // reserved
    [InlineData("xn--acme.app.example.com")]        // a label no tenant can hold
    [InlineData("")]
    public void Finds_no_subdomain_in_a_host_that_names_no_tenant(string host)
    {
        Assert.Null(TenantAddress.SubdomainOf(host, "app.example.com"));
    }

    [Theory]
    [InlineData("https://app.example.com", "https://acme.app.example.com")]
    [InlineData("https://app.example.com/", "https://acme.app.example.com")]
    [InlineData("http://localhost:5173", "http://acme.localhost:5173")]
    public void Writes_the_sign_in_address_from_the_base_url(string baseUrl, string expected)
    {
        Assert.Equal(expected, TenantAddress.SignInUrl("acme", TenantAddress.ParseBaseUrl(baseUrl)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app.example.com")]               // no scheme
    [InlineData("ftp://app.example.com")]
    [InlineData("https://app.example.com/shell")] // a path would be lost from every address
    [InlineData("https://app.example.com/?x=1")]
    public void Refuses_a_base_url_it_cannot_build_addresses_from(string? baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => TenantAddress.ParseBaseUrl(baseUrl));
    }
}
