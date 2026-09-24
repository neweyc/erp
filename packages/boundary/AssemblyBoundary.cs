using System.Reflection;

namespace AppPlatform.Boundary;

/// <summary>
/// Reference rules between deployables. The platform must not reference core; an app must
/// not reference another app. Enforced against compiled metadata rather than csproj text,
/// so a transitive reference is caught as well as a direct one.
/// </summary>
public static class AssemblyBoundary
{
    public static IReadOnlyList<string> FindForbiddenReferences(
        Assembly assembly, IEnumerable<string> forbiddenNamePrefixes)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(forbiddenNamePrefixes);

        var forbidden = forbiddenNamePrefixes.ToArray();

        return [.. ReferenceClosure(assembly)
            .Where(name => forbidden.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .Select(name => $"{assembly.GetName().Name} references {name}")
            .Distinct(StringComparer.Ordinal)
            .Order()];
    }

    /// <summary>
    /// The full reference graph, direct and transitive. Public because the difference
    /// between this and <see cref="Assembly.GetReferencedAssemblies"/> is the whole point
    /// of the rule — a direct-only check passes while platform -> shared -> core quietly
    /// reintroduces exactly what is forbidden — and a test that cannot see both cannot
    /// prove the distinction is being made.
    ///
    /// Assemblies that fail to load are skipped: they cannot be the violation, and
    /// throwing would turn an unrelated packaging problem into an architecture failure.
    /// </summary>
    public static IEnumerable<string> ReferenceClosure(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([root]);

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name is null || !seen.Add(name)) continue;

                yield return name;

                Assembly? next = null;
                try { next = Assembly.Load(reference); }
                catch (FileNotFoundException) { }
                catch (FileLoadException) { }
                catch (BadImageFormatException) { }

                if (next is not null) queue.Enqueue(next);
            }
        }
    }
}
