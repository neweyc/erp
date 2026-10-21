using System.Security.Cryptography;
using System.Text;

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
/// licensed (see <see cref="Access.Licensed"/>).
/// </summary>
public static class Permissions
{
    /// <summary>Core is always on and has no entitlement; its permissions and roles use this prefix.</summary>
    public const string CoreApp = "core";

    public static class Core
    {
        public const string EmployeeRead = "core.employee.read";
        public const string EmployeeCreate = "core.employee.create";
        public const string UserRead = "core.user.read";
        public const string UserManage = "core.user.manage";
        public const string RoleManage = "core.role.manage";
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
    public static string AppOf(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? "" : name[..dot];
    }
}

/// <param name="Description">Shown in the role editor, so an administrator can choose without a manual.</param>
public sealed record PermissionDefinition(string Name, string Description)
{
    public string App => Permissions.AppOf(Name);
}

/// <summary>
/// Every permission with its description. Customers combine these into roles; they never invent
/// them, because a permission only means something when an endpoint checks it.
/// </summary>
public static class PermissionCatalogue
{
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Permissions.Core.EmployeeRead, "See the employee roster"),
        new(Permissions.Core.EmployeeCreate, "Add employees"),
        new(Permissions.Core.UserRead, "See accounts, their roles, and the roles defined"),
        new(Permissions.Core.UserManage, "Invite people and change anyone's roles"),
        new(Permissions.Core.RoleManage, "Create, edit and delete the organization's own roles"),

        new(Permissions.Tickets.TicketRead, "See tickets"),
        new(Permissions.Tickets.TicketCreate, "Raise tickets"),
        new(Permissions.Tickets.TicketAssign, "Assign tickets"),
        new(Permissions.Tickets.TicketClose, "Close tickets"),

        new(Permissions.Ledger.Read, "See accounts, entries, the trial balance and periods"),
        new(Permissions.Ledger.EntryPost, "Post journal entries"),
        new(Permissions.Ledger.EntryReverse, "Reverse journal entries"),
        new(Permissions.Ledger.AccountCreate, "Add accounts to the chart"),
        new(Permissions.Ledger.PeriodClose, "Close accounting periods"),
    ];

    public static readonly IReadOnlySet<string> Names =
        All.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
}

/// <summary>A role defined in code: the same in every tenant, and never edited or deleted by one.</summary>
/// <param name="Key">
/// <c>&lt;app&gt;:&lt;name&gt;</c>, stored in <c>identity.user.roles</c>. Unique across apps, and never
/// mistaken for a custom role's <c>rol_…</c> id. A colon, where permissions use dots, so a role key
/// and a permission name can never be confused — and so core's source, which must not contain
/// another service's schema name followed by a dot (BoundaryTests), can name built-in app roles.
/// </param>
/// <param name="Name">The display name. A custom role may not reuse it.</param>
public sealed record BuiltInRole(string Key, string Name, string Description, IReadOnlyList<string> Permissions)
{
    public string App => Key[..Key.IndexOf(':', StringComparison.Ordinal)];
}

/// <summary>
/// The built-in roles. Each covers one app. Customers add their own alongside (docs/permissions.md
/// §1); these stay locked so that a release adding a permission here reaches every tenant at once.
/// </summary>
public static class BuiltInRoles
{
    public const string CoreAdmin = "core:admin";
    public const string CoreMember = "core:member";

    public static readonly IReadOnlyList<BuiltInRole> All =
    [
        new(CoreAdmin, "Administrator", "Manages people, accounts and roles.",
        [
            Permissions.Core.EmployeeRead, Permissions.Core.EmployeeCreate,
            Permissions.Core.UserRead, Permissions.Core.UserManage, Permissions.Core.RoleManage,
        ]),
        new("core:manager", "Manager", "Maintains the employee roster.",
        [
            Permissions.Core.EmployeeRead, Permissions.Core.EmployeeCreate,
        ]),
        new(CoreMember, "Member", "Sees the employee roster.",
        [
            Permissions.Core.EmployeeRead,
        ]),

        new("tickets:requester", "Requester", "Raises and follows tickets.",
        [
            Permissions.Tickets.TicketRead, Permissions.Tickets.TicketCreate,
        ]),
        new("tickets:agent", "Agent", "Works tickets: assigns and closes them.",
        [
            Permissions.Tickets.TicketRead, Permissions.Tickets.TicketCreate,
            Permissions.Tickets.TicketAssign, Permissions.Tickets.TicketClose,
        ]),

        new("ledger:viewer", "Viewer", "Reads the books.",
        [
            Permissions.Ledger.Read,
        ]),
        new("ledger:accountant", "Accountant", "Posts and reverses journal entries.",
        [
            Permissions.Ledger.Read, Permissions.Ledger.EntryPost, Permissions.Ledger.EntryReverse,
        ]),
        new("ledger:controller", "Controller", "Everything an accountant does, plus the chart of accounts and closing periods.",
        [
            Permissions.Ledger.Read, Permissions.Ledger.EntryPost, Permissions.Ledger.EntryReverse,
            Permissions.Ledger.AccountCreate, Permissions.Ledger.PeriodClose,
        ]),
    ];

    public static BuiltInRole? Find(string key) => All.FirstOrDefault(r => r.Key == key);

    /// <summary>A custom role's key is its public id. Anything else is (or was) a built-in key.</summary>
    public static bool IsCustomKey(string key) => key.StartsWith("rol_", StringComparison.Ordinal);
}

/// <summary>
/// What a user may do. The one implementation of that rule: the session evaluator, sign-in and the
/// last-administrator check all call it, so they cannot disagree.
/// </summary>
public static class Access
{
    /// <summary>
    /// Everything the user's roles grant, before licensing: each built-in role's permissions, plus
    /// <paramref name="customRolePermissions"/> (the union of their custom roles', read from the
    /// database). Fails closed: an unknown role key grants nothing, and a permission this build
    /// does not know (removed from the catalogue, or written by a newer build) is dropped. Null
    /// entries, which a text[] column allows, are skipped rather than thrown on, because this runs
    /// on every request.
    /// </summary>
    public static IReadOnlySet<string> Granted(
        IEnumerable<string?> roleKeys, IEnumerable<string?> customRolePermissions)
    {
        var granted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in roleKeys)
        {
            if (key is not null && BuiltInRoles.Find(key) is { } role)
                granted.UnionWith(role.Permissions);
        }

        foreach (var permission in customRolePermissions)
        {
            if (permission is not null && PermissionCatalogue.Names.Contains(permission))
                granted.Add(permission);
        }

        return granted;
    }

    /// <summary>
    /// The part of <paramref name="granted"/> usable now: core permissions, and those of apps the
    /// tenant currently licenses. Delicensing an app therefore removes access without touching
    /// anyone's roles, and relicensing restores it.
    /// </summary>
    public static IReadOnlySet<string> Licensed(
        IReadOnlySet<string> granted, IReadOnlyCollection<string> licensedApps)
        => granted
            .Where(p => Permissions.AppOf(p) is var app && (app == Permissions.CoreApp || licensedApps.Contains(app)))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The access claim: a short hash of <see cref="Granted"/>, recorded in the cookie at sign-in and
    /// compared on every request. Any change to what the user's roles grant (a role assigned, a
    /// role edited) is <c>role_changed</c>, which sends the shell back to sign-in so its navigation
    /// matches. Before licensing on purpose: licensing changes should not sign everyone out.
    /// Security does not rest on it; permissions are recomputed from the live rows regardless.
    /// </summary>
    public static string Stamp(IReadOnlySet<string> granted)
    {
        var canonical = string.Join('\n', granted.Order(StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(hash, 0, 16);
    }
}
