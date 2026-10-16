namespace AppPlatform.Auth;

/// <summary>
/// Every permission, by app. Endpoints name one of these; nothing names a role. Design:
/// docs/permissions.md.
///
/// The strings are API surface — the session returns them and the UI checks them — so renaming
/// one is a breaking change. Mirrored in <c>packages/web-api/src/permissions.ts</c>;
/// <c>PermissionCatalogueTests</c> fails when the two disagree.
///
/// Format: <c>&lt;app&gt;.&lt;thing&gt;.&lt;verb&gt;</c>. The prefix before the first dot is the app the
/// permission belongs to, and it is load-bearing: a permission is granted only while its app is
/// licensed (see <see cref="RoleCatalogue.PermissionsFor"/>).
/// </summary>
public static class Permissions
{
    public static class Core
    {
        public const string EmployeeRead = "core.employee.read";
        public const string EmployeeCreate = "core.employee.create";
        public const string UserRead = "core.user.read";
        public const string UserManage = "core.user.manage";
    }

    public static class Tickets
    {
        public const string TicketRead = "tickets.ticket.read";
        public const string TicketCreate = "tickets.ticket.create";
        public const string TicketAssign = "tickets.ticket.assign";
        public const string TicketClose = "tickets.ticket.close";
    }

    public static class Ledger
    {
        public const string Read = "ledger.read";
        public const string EntryPost = "ledger.entry.post";
        public const string EntryReverse = "ledger.entry.reverse";
        public const string AccountCreate = "ledger.account.create";
        public const string PeriodClose = "ledger.period.close";
    }

    /// <summary>The app a permission belongs to: the text before its first dot.</summary>
    public static string AppOf(string permission) => permission[..permission.IndexOf('.', StringComparison.Ordinal)];
}

/// <summary>A fixed, named bundle of permissions in one app.</summary>
/// <param name="Description">Shown on the Users page, so an administrator can choose without a manual.</param>
public sealed record RoleDefinition(
    string App, string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>
/// The roles, and what each grants. Code, not data: see docs/permissions.md §1 for why, and for
/// what changes if tenants ever define their own.
///
/// A user holds one CORE role (<c>identity.user.role</c>) and at most one role in each app
/// (<c>identity.user.app_roles</c>, entries like <c>tickets:agent</c>).
/// </summary>
public static class RoleCatalogue
{
    /// <summary>Core is always on and has no entitlement; its roles are keyed under this name.</summary>
    public const string CoreApp = "core";

    public const string Admin = "admin";

    public static readonly IReadOnlyList<RoleDefinition> All =
    [
        new(CoreApp, Admin, "Manages people, accounts and roles.",
        [
            Permissions.Core.EmployeeRead, Permissions.Core.EmployeeCreate,
            Permissions.Core.UserRead, Permissions.Core.UserManage,
        ]),
        new(CoreApp, "manager", "Maintains the employee roster.",
        [
            Permissions.Core.EmployeeRead, Permissions.Core.EmployeeCreate,
        ]),
        new(CoreApp, "member", "Sees the employee roster.",
        [
            Permissions.Core.EmployeeRead,
        ]),

        new("tickets", "requester", "Raises and follows tickets.",
        [
            Permissions.Tickets.TicketRead, Permissions.Tickets.TicketCreate,
        ]),
        new("tickets", "agent", "Works tickets: assigns and closes them.",
        [
            Permissions.Tickets.TicketRead, Permissions.Tickets.TicketCreate,
            Permissions.Tickets.TicketAssign, Permissions.Tickets.TicketClose,
        ]),

        new("ledger", "viewer", "Reads the books.",
        [
            Permissions.Ledger.Read,
        ]),
        new("ledger", "accountant", "Posts and reverses journal entries.",
        [
            Permissions.Ledger.Read, Permissions.Ledger.EntryPost, Permissions.Ledger.EntryReverse,
        ]),
        new("ledger", "controller", "Everything an accountant does, plus the chart of accounts and closing periods.",
        [
            Permissions.Ledger.Read, Permissions.Ledger.EntryPost, Permissions.Ledger.EntryReverse,
            Permissions.Ledger.AccountCreate, Permissions.Ledger.PeriodClose,
        ]),
    ];

    /// <summary>Every permission any role grants.</summary>
    public static readonly IReadOnlySet<string> AllPermissions =
        All.SelectMany(role => role.Permissions).ToHashSet(StringComparer.Ordinal);

    public static RoleDefinition? Find(string app, string role)
        => All.FirstOrDefault(r => r.App == app && r.Name == role);

    public static IEnumerable<RoleDefinition> RolesIn(string app) => All.Where(r => r.App == app);

    /// <summary>
    /// What a user may do right now: the core role's permissions, plus each app role's permissions
    /// for apps the tenant currently licenses.
    ///
    /// Two deliberate refusals. An app role in an UNLICENSED app grants nothing, so delicensing
    /// removes access without touching anyone's roles. An UNKNOWN role name grants nothing — an
    /// older build reading a role a newer one wrote, or a role since removed — because failing
    /// closed is recoverable and failing open is not.
    /// </summary>
    public static IReadOnlySet<string> PermissionsFor(
        string coreRole,
        IReadOnlyDictionary<string, string> appRoles,
        IReadOnlyCollection<string> licensedApps)
    {
        var granted = new HashSet<string>(StringComparer.Ordinal);

        if (Find(CoreApp, coreRole) is { } core)
            granted.UnionWith(core.Permissions);

        foreach (var (app, role) in appRoles)
        {
            if (app == CoreApp || !licensedApps.Contains(app)) continue;

            if (Find(app, role) is { } definition)
                granted.UnionWith(definition.Permissions);
        }

        return granted;
    }
}

/// <summary>
/// The <c>identity.user.app_roles</c> encoding: one <c>app:role</c> string per app.
/// </summary>
public static class AppRoles
{
    public static string Encode(string app, string role) => $"{app}:{role}";

    /// <summary>
    /// Malformed entries are skipped rather than thrown on. This runs on every authenticated
    /// request, and one bad row must not turn into a 500 for that user — it simply grants
    /// nothing, like an unknown role. A repeated app keeps the first entry; the write path
    /// never produces one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Decode(IEnumerable<string?> encoded)
    {
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in encoded)
        {
            // text[] NOT NULL still allows a NULL element, written by hand or by an older script.
            if (entry is null) continue;

            var separator = entry.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1) continue;

            roles.TryAdd(entry[..separator], entry[(separator + 1)..]);
        }

        return roles;
    }

    /// <summary>
    /// The access claim: core role and sorted app roles as one string, recorded in the cookie at
    /// sign-in and compared on every request. A mismatch is <c>role_changed</c>, which sends the
    /// shell back to sign-in so its navigation matches what the user may now do. Security does
    /// not rest on it — permissions are recomputed from the live row regardless.
    /// </summary>
    public static string AccessStamp(string coreRole, IReadOnlyDictionary<string, string> appRoles)
    {
        var parts = appRoles
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Encode(pair.Key, pair.Value));

        return string.Join(";", [coreRole, .. parts]);
    }
}
