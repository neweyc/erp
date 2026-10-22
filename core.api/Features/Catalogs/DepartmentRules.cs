using AppPlatform.Core.Data;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>
/// The rules for the department tree, shared by adding and editing a department. Each takes the
/// tenant's whole list of departments, read under the catalog lock, so nothing can change the tree
/// between the check and the save.
///
/// Three things the tree must never hold:
///  - a loop (a department inside itself, however far down), which would make every path endless;
///  - a branch deeper than <see cref="Department.MaxDepth"/>;
///  - an active department under an inactive one, so every active department's whole path is in use
///    and a picker never offers "Retired › Still here".
/// </summary>
public static class DepartmentRules
{
    /// <summary>
    /// Why <paramref name="department"/> (null when adding one) cannot have this name, parent and
    /// status, or null when it can. Name clashes are reported separately, by <see cref="NameTaken"/>.
    /// </summary>
    public static string? Problem(
        Department? department, Department? parent, bool active, IReadOnlyList<Department> all)
    {
        var byId = all.ToDictionary(d => d.Id);

        if (department is not null && parent is not null
            && (parent.Id == department.Id || AncestorsOf(parent, byId).Any(a => a.Id == department.Id)))
        {
            return "A department cannot sit inside itself or one of its own sub-departments.";
        }

        // The deepest point of the moved branch: the parent's depth, plus the branch's own height.
        var parentDepth = parent is null ? 0 : AncestorsOf(parent, byId).Count() + 1;
        var height = department is null ? 1 : HeightOf(department, all);
        if (parentDepth + height > Department.MaxDepth)
            return $"Departments can be nested at most {Department.MaxDepth} levels deep.";

        if (active && parent is { Active: false })
            return $"{parent.Name} is inactive. Reactivate it first, or choose another parent.";

        if (!active && department is not null && all.Any(d => d.ParentId == department.Id && d.Active))
            return "It has active sub-departments. Deactivate or move them first.";

        return null;
    }

    /// <summary>Whether a sibling under <paramref name="parent"/> other than <paramref name="department"/> already has the name.</summary>
    public static bool NameTaken(Department? department, string name, Department? parent, IReadOnlyList<Department> all)
        => all.Any(d => d.ParentId == parent?.Id && d.Id != department?.Id && CatalogRules.SameName(d.Name, name));

    /// <summary>The sentence for a name clash under <paramref name="parent"/>.</summary>
    public static string NameTakenMessage(Department? parent)
        => parent is null
            ? "Another top-level department already has that name."
            : $"Another department in {parent.Name} already has that name.";

    /// <summary>
    /// The departments in tree order (each followed by its sub-departments, siblings by name), with
    /// their depth (1 at the top) and path ("Engineering › Platform"). The order a picker shows them in.
    /// </summary>
    public static List<(Department Department, int Depth, string Path)> InTreeOrder(IReadOnlyList<Department> all)
    {
        var children = all.ToLookup(d => d.ParentId);
        var ordered = new List<(Department, int, string)>();

        void Visit(Guid? parentId, int depth, string prefix)
        {
            foreach (var d in children[parentId].OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var path = prefix.Length == 0 ? d.Name : $"{prefix} › {d.Name}";
                ordered.Add((d, depth, path));
                // The depth limit stops a runaway if the data ever held a loop the rules missed.
                if (depth < Department.MaxDepth) Visit(d.Id, depth + 1, path);
            }
        }

        Visit(null, 1, "");
        return ordered;
    }

    /// <summary>The path to every department, by id: what a placement shows.</summary>
    public static Dictionary<Guid, string> Paths(IReadOnlyList<Department> all)
        => InTreeOrder(all).ToDictionary(x => x.Department.Id, x => x.Path);

    private static IEnumerable<Department> AncestorsOf(Department department, Dictionary<Guid, Department> byId)
    {
        var current = department;
        // Bounded by the number of departments, so a loop already in the data cannot hang the request.
        for (var steps = 0; steps < byId.Count && current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent); steps++)
        {
            yield return parent;
            current = parent;
        }
    }

    /// <summary>Levels from this department down to its deepest sub-department, counting itself as 1.</summary>
    private static int HeightOf(Department department, IReadOnlyList<Department> all)
    {
        var children = all.ToLookup(d => d.ParentId);
        int Height(Guid id, int guard)
            => guard > Department.MaxDepth ? guard : 1 + children[id].Select(c => Height(c.Id, guard + 1)).DefaultIfEmpty(0).Max();
        return Height(department.Id, 1);
    }
}
