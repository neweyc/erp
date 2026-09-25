namespace AppPlatform.Entitlements;

/// <summary>
/// The licensable apps. Mirrored in the shell's app registry — keep the two in sync.
///
/// Core is deliberately absent: it is always on, has no entitlement row, and a constant here
/// would invite someone to write <c>.RequireApp(Apps.Core)</c>, which would then be one
/// misconfigured row away from locking every tenant out of their own employee list.
/// </summary>
public static class Apps
{
    public const string Tickets = "tickets";

    public static readonly string[] All = [Tickets];
}
