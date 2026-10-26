using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AppPlatform.Tenancy;

/// <summary>
/// A tenant's web address: one subdomain of the deployment's base domain, so
/// <c>acme-fire.app.example.com</c> is the tenant whose subdomain is <c>acme-fire</c> when the
/// base domain is <c>app.example.com</c>.
///
/// The address is how an anonymous request (sign-in) finds its tenant. It is never taken from a
/// header or the body, which would let a caller choose whose accounts to try. Platform and core
/// both apply these rules: platform when an operator chooses an address, core when provisioning
/// writes one and when sign-in reads one.
/// </summary>
public static partial class TenantAddress
{
    public const int MinLength = 3;
    public const int MaxLength = 40;

    /// <summary>
    /// Names the deployment uses for itself, or that a customer could pass off as ours
    /// (<c>login.app.example.com</c>). Refused for every tenant.
    /// </summary>
    private static readonly HashSet<string> Reserved =
    [
        "admin", "api", "app", "assets", "auth", "billing", "cdn", "console", "dev", "docs", "help",
        "internal", "localhost", "login", "mail", "operator", "platform", "signin", "smtp", "sso",
        "staging", "static", "status", "support", "test", "www",
    ];

    /// <summary>
    /// Lowercase letters, digits and single hyphens; starts with a letter and ends with a letter or
    /// digit. Narrower than DNS allows, on purpose: no leading digit (an all-digit name reads as an
    /// id), and no double hyphen, which DNS reserves for encoded international names (<c>xn--</c>).
    /// </summary>
    [GeneratedRegex("^[a-z](?:[a-z0-9]|-(?!-))*[a-z0-9]$")]
    private static partial Regex Shape();

    /// <summary>
    /// Checks an operator's choice. Gives the subdomain as stored (trimmed, lowercase), or the
    /// reason it cannot be used, written for the operator.
    /// </summary>
    public static bool TryNormalize(
        string? input, [NotNullWhen(true)] out string? subdomain, [NotNullWhen(false)] out string? problem)
    {
        var candidate = input?.Trim().ToLowerInvariant() ?? "";
        subdomain = null;

        if (candidate.Length is < MinLength or > MaxLength)
            problem = $"An address must be {MinLength} to {MaxLength} characters.";
        else if (!Shape().IsMatch(candidate))
            problem = "An address may use letters, digits and single hyphens, and must start with a letter " +
                "and end with a letter or digit.";
        else if (Reserved.Contains(candidate))
            problem = $"\"{candidate}\" is reserved. Choose another address.";
        else
        {
            subdomain = candidate;
            problem = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The subdomain a request's host names, or null when it names none: the bare base domain, a
    /// host under another domain, more than one label (<c>a.b.app.example.com</c>), or a label
    /// that breaks the rules above and so cannot belong to any tenant.
    /// </summary>
    public static string? SubdomainOf(string host, string baseDomain)
    {
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        var suffix = "." + baseDomain.Trim().TrimEnd('.').ToLowerInvariant();

        if (!normalizedHost.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var label = normalizedHost[..^suffix.Length];
        if (label.Contains('.'))
            return null;

        return TryNormalize(label, out var subdomain, out _) ? subdomain : null;
    }

    /// <summary>
    /// Reads the deployment's <c>Tenant:BaseUrl</c>: the shell's address with no tenant in it,
    /// <c>https://app.example.com</c> (or <c>http://localhost:5173</c> in development). Its host is
    /// the base domain every tenant's address sits under. Throws at startup on anything else, since
    /// without it nobody can sign in.
    /// </summary>
    public static Uri ParseBaseUrl(string? configured)
    {
        if (Uri.TryCreate(configured, UriKind.Absolute, out var url)
            && url.Scheme is "http" or "https"
            // A user name or password would appear in every sign-in address the console shows.
            && string.IsNullOrEmpty(url.UserInfo)
            && url.AbsolutePath == "/" && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment))
        {
            return url;
        }

        throw new InvalidOperationException(
            "Tenant:BaseUrl is required: the shell's address with no tenant in it, such as " +
            "https://app.example.com (http://localhost:5173 in development). Tenants sign in at " +
            "subdomains of its host, and sign-in reads the tenant from the request's host.");
    }

    /// <summary>
    /// The refusal when another tenant has, or once had, the address. One wording wherever it is
    /// refused: platform's rename and core's provisioning, whose message platform passes on.
    /// </summary>
    public static string TakenMessage(string subdomain)
        => $"The address \"{subdomain}\" belongs to another tenant, now or before. Choose another.";

    /// <summary>Where a tenant's people sign in: <c>https://acme-fire.app.example.com</c>.</summary>
    public static string SignInUrl(string subdomain, Uri baseUrl)
        => new UriBuilder(baseUrl) { Host = $"{subdomain}.{baseUrl.Host}" }.Uri.GetLeftPart(UriPartial.Authority);
}
