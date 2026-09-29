using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPlatform.Auth;

/// <summary>
/// Rate limiting for every anonymous endpoint — sign-in, invitation acceptance, and whatever
/// anonymous endpoint is added next — per client IP address.
///
/// Anonymous endpoints are the ones an attacker can call without an account, so they are where
/// password guessing and token guessing happen. The limit applies to any endpoint carrying
/// <see cref="IAllowAnonymous"/> metadata (<c>.AllowAnonymous()</c>), found at request time, so a
/// new anonymous endpoint is covered without anyone remembering to opt it in. Authenticated
/// endpoints are not limited here.
///
/// The limit is only as good as the client address it partitions on, which is why the trusted
/// proxy list lives here too. Behind nginx, every request arrives from nginx's own address; with
/// nothing trusted, every user on the platform shares ONE bucket and ten sign-ins a minute is the
/// limit for everyone. So the forwarded client address is used — but ONLY when the request came
/// from a configured proxy, because anyone can send an X-Forwarded-For header, and honouring it
/// from anywhere lets an attacker give every request a fresh address and a fresh bucket.
///
/// Not done here, and recorded as such: a limit per ACCOUNT. Guessing spread across many
/// addresses (credential stuffing) passes a per-address limit.
/// </summary>
public static class AnonymousRateLimiting
{
    /// <summary>Requests per minute, per client address, to anonymous endpoints.</summary>
    public const string PermitsPerMinuteKey = "RateLimits:AnonymousPerMinute";

    /// <summary>
    /// Comma-separated IP addresses of the reverse proxies whose X-Forwarded-For is believed.
    /// Required in Production, where the APIs sit behind nginx.
    /// </summary>
    public const string TrustedProxiesKey = "ForwardedHeaders:TrustedProxies";

    public const int DefaultPermitsPerMinute = 10;

    public static IServiceCollection AddAnonymousRateLimiting(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var permitsPerMinute = configuration.GetValue(PermitsPerMinuteKey, DefaultPermitsPerMinute);
        var trustedProxies = ParseTrustedProxies(configuration[TrustedProxiesKey]);

        // Checked here because the limiter only builds a bucket on the first anonymous request: a
        // bad value would otherwise start cleanly and then fail every sign-in with a 500.
        if (permitsPerMinute < 1)
            throw new InvalidOperationException($"{PermitsPerMinuteKey} must be at least 1; it is {permitsPerMinute}.");

        // Refused at startup, not discovered under load: in Production the APIs sit behind a
        // proxy, and without this every user shares one bucket (see the class comment).
        if (environment.IsProduction() && trustedProxies.Count == 0)
        {
            throw new InvalidOperationException(
                $"{TrustedProxiesKey} is required in Production: the address of the reverse proxy in " +
                "front of this service. Without it every client appears to come from the proxy and " +
                "shares one rate limit.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // With no proxy configured, forwarded headers are not processed AT ALL. Not merely an
            // empty trust list: the framework reads an empty list as "trust every sender", which
            // would let anyone choose their own address, and a fresh limit, per request.
            options.ForwardedHeaders = trustedProxies.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor;

            // The framework trusts loopback by default. Cleared, so nothing is trusted unless it
            // is listed: an unlisted source's X-Forwarded-For is ignored, not believed.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in trustedProxies) options.KnownProxies.Add(proxy);

            // One proxy hop. The address nginx appends is the one it saw; anything earlier in the
            // header was written by the client and proves nothing.
            options.ForwardLimit = 1;
        });

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var endpoint = context.GetEndpoint();
                if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is null)
                    return RateLimitPartition.GetNoLimiter("not-anonymous");

                return RateLimitPartition.GetFixedWindowLimiter(ClientKey(context.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    // Refused at once rather than queued: a queued guess is still a guess.
                    QueueLimit = 0,
                });
            });

            options.OnRejected = async (rejected, _) =>
            {
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    rejected.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await AuthProblemResponse.WriteAsync(rejected.HttpContext, AuthProblem.RateLimited);
            };
        });

        return services;
    }

    /// <summary>
    /// Before authentication, so a flood of anonymous requests is refused before it costs a
    /// database lookup. Forwarded headers first, because the limiter partitions on the address
    /// they establish.
    /// </summary>
    public static IApplicationBuilder UseAnonymousRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseForwardedHeaders();
        app.UseRateLimiter();

        return app;
    }

    /// <summary>
    /// The bucket a client address belongs to.
    ///
    /// IPv6 is grouped by its /64 prefix, not the full address. One subscriber is routinely given
    /// a whole /64, which is 2^64 addresses; limited per address, rotating through them would
    /// give a fresh limit on every request. An IPv4 address that arrives in IPv6 form
    /// (::ffff:203.0.113.1) is the same client as 203.0.113.1 and shares its bucket. No address at
    /// all (an in-process test host) shares one bucket rather than going unlimited.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null) return "unknown";

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static List<IPAddress> ParseTrustedProxies(string? configured)
    {
        var proxies = new List<IPAddress>();

        foreach (var entry in (configured ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(entry, out var address))
                throw new InvalidOperationException($"{TrustedProxiesKey}: '{entry}' is not an IP address.");

            proxies.Add(address);
        }

        return proxies;
    }
}
