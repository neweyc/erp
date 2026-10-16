using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;

namespace AppPlatform.Core.Features.Roles;

/// <summary>
/// The rules shared by every feature that assigns or defines roles (docs/permissions.md §6). Kept in
/// one place because the features must agree exactly: if inviting accepted a role that assigning
/// refused, or the last-administrator check counted differently in two places, the gap between them
/// would be the hole.
///
/// Every caller runs these under the tenant's role-change lock, so what they read cannot change
/// before the caller commits.
/// </summary>
public static class RoleRules
{
    /// <summary>The fields a custom role is defined by, shared by create and edit.</summary>
    public record RoleDefinitionCommand(string? Name, string? Description, List<string?>? Permissions);

    /// <summary>A definition that passed validation: trimmed, de-duplicated, sorted.</summary>
    public record ValidDefinition(string Name, string Description, string[] Permissions);

    /// <summary>Custom roles by key (their public id), as <see cref="User.Roles"/> refers to them.</summary>
    public static Dictionary<string, Role> ByKey(IEnumerable<Role> customRoles)
        => customRoles.ToDictionary(r => r.PublicId, StringComparer.Ordinal);

    /// <summary>Null when <paramref name="key"/> may be given to someone now; otherwise why not.</summary>
    public static string? CannotAssign(
        string key, IReadOnlyDictionary<string, Role> customRoles, IReadOnlyCollection<string> licensedApps)
    {
        if (BuiltInRoles.IsCustomKey(key))
            return customRoles.ContainsKey(key) ? null : $"'{key}' is not one of your organisation's roles.";

        if (BuiltInRoles.Find(key) is not { } builtIn)
            return $"'{key}' is not a role.";

        // A built-in role in an unlicensed app would grant nothing today, and something
        // unexpected on the day the app is licensed, long after whoever chose it has forgotten.
        if (builtIn.App != Permissions.CoreApp && !licensedApps.Contains(builtIn.App))
            return $"Your organisation has not licensed {builtIn.App}, so its roles cannot be given.";

        return null;
    }

    /// <summary>What a set of role keys grants, before licensing. The same rule sessions use (<see cref="Access"/>).</summary>
    /// <remarks>
    /// A stored array can hold NULL elements (text[] allows them), and a dictionary lookup throws on
    /// null, so they are skipped here as <see cref="Access.Granted"/> skips them: one bad row must not
    /// stop a user signing in, or stop the last-administrator check counting everyone else.
    /// </remarks>
    public static IReadOnlySet<string> GrantedBy(
        IReadOnlyCollection<string?> roleKeys, IReadOnlyDictionary<string, Role> customRoles)
        => Access.Granted(
            roleKeys,
            roleKeys
                .Where(key => key is not null && customRoles.ContainsKey(key))
                .SelectMany(key => customRoles[key!].Permissions));

    /// <summary>
    /// Trimmed, distinct keys or names from a request, or null when the list holds a null or blank
    /// entry. JSON can send <c>[null]</c> for a list of strings; that is a bad request, not a 500.
    /// </summary>
    public static List<string>? CleanList(IEnumerable<string?>? values)
    {
        var cleaned = new List<string>();
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            cleaned.Add(value.Trim());
        }
        return cleaned.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// True when a change would leave NO active user who can manage users, where before there was
    /// one. Without such a user nobody can fix the tenant's access again without operator help.
    /// <paramref name="rolesAfter"/> gives each user's roles once the change is made, and
    /// <paramref name="customRolesAfter"/> the custom roles; pass the current values for whatever
    /// the change leaves alone. A tenant that had nobody beforehand is not blocked: a change can
    /// then only help. <c>core.user.manage</c> is a core permission, so licensing never matters.
    /// </summary>
    public static bool RemovesLastAdmin(
        IReadOnlyCollection<User> users,
        IReadOnlyDictionary<string, Role> customRoles,
        Func<User, IReadOnlyCollection<string?>> rolesAfter,
        IReadOnlyDictionary<string, Role> customRolesAfter)
        => CanManageUsers(users, u => u.Roles, customRoles)
           && !CanManageUsers(users, rolesAfter, customRolesAfter);

    private static bool CanManageUsers(
        IEnumerable<User> users,
        Func<User, IReadOnlyCollection<string?>> rolesOf,
        IReadOnlyDictionary<string, Role> customRoles)
        => users
            .Where(u => u.Active)
            .Any(u => GrantedBy(rolesOf(u), customRoles).Contains(Permissions.Core.UserManage));

    public static CommandResult LastAdmin() => CommandResult.Conflict(
        CoreProblems.LastAdmin,
        "Nobody else active could manage users after this change. Give someone else that permission first.");

    /// <summary>
    /// Validates a role definition, or returns the failure to send back. <paramref name="existing"/>
    /// is the role being edited, or null when creating one.
    /// </summary>
    public static (CommandResult? Problem, ValidDefinition? Definition) Validate(
        RoleDefinitionCommand cmd,
        Role? existing,
        IReadOnlyCollection<Role> customRoles,
        IReadOnlyCollection<string> licensedApps)
    {
        var name = cmd.Name?.Trim() ?? "";
        if (name.Length == 0) return Invalid("A role needs a name.");
        if (name.Length > Role.NameMaxLength) return Invalid($"A role name can be at most {Role.NameMaxLength} characters.");

        // Two roles called "Agent" would make the Users page ambiguous.
        if (BuiltInRoles.All.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Invalid($"'{name}' is the name of a built-in role. Choose another.");

        if (customRoles.Any(r => r.Id != existing?.Id && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return (CommandResult.Conflict(CoreProblems.RoleNameTaken, $"There is already a role called '{name}'."), null);
        }

        var description = cmd.Description?.Trim() ?? "";
        if (description.Length > Role.DescriptionMaxLength)
            return Invalid($"A description can be at most {Role.DescriptionMaxLength} characters.");

        if (CleanList(cmd.Permissions) is not { } requested)
            return Invalid("Permission names cannot be empty.");

        var held = existing?.Permissions ?? [];
        foreach (var permission in requested)
        {
            if (!PermissionCatalogue.Names.Contains(permission))
                return Invalid($"'{permission}' is not a permission.");

            // Adding a permission in an unlicensed app is refused, for the same reason as
            // assigning a built-in role there. One the role already holds is not "added".
            var app = Permissions.AppOf(permission);
            if (app != Permissions.CoreApp && !licensedApps.Contains(app) && !held.Contains(permission))
                return Invalid($"Your organisation has not licensed {app}, so its permissions cannot be added.");
        }

        // Kept: what the role holds in apps the tenant no longer licenses. The editor shows only
        // licensed apps, so a save must not erase what it never displayed.
        // A NULL element a hand-written row may hold is dropped, not kept.
        var kept = held.Where(p =>
            p is not null && Permissions.AppOf(p) is var app && app != Permissions.CoreApp && !licensedApps.Contains(app));

        var permissions = requested.Union(kept, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // Checked AFTER the kept permissions are merged: renaming a role whose permissions are all
        // in an app since unlicensed sends none, and the role still grants something.
        if (permissions.Length == 0) return Invalid("A role needs at least one permission.");

        return (null, new ValidDefinition(name, description, permissions));
    }

    private static (CommandResult?, ValidDefinition?) Invalid(string message)
        => (CommandResult.Invalid(CoreProblems.ValidationFailed, message), null);
}
