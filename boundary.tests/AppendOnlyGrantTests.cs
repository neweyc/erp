using System.Reflection;
using System.Text.RegularExpressions;
using AppPlatform.Boundary;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// Keeps the two layers of append-only in step. An entity marked <see cref="IAppendOnly"/> is refused
/// an edit by the code; its table must ALSO be listed in 02-grants.sql (which takes UPDATE and DELETE
/// away from the runtime role) and in 99-verify.sql (which reports it if they come back). Otherwise
/// the code says "append-only" while the database would accept a rewrite from anything else.
/// </summary>
public partial class AppendOnlyGrantTests
{
    /// <summary>
    /// Every registered service's model, built through its design-time factory — so a new service
    /// is covered the moment it joins BoundaryRegistry, with no list here to forget to extend.
    /// </summary>
    private static List<IModel> ServiceModels()
        => [.. BoundaryRegistry.Services.SelectMany(service =>
        {
            var name = service.ProjectDirectory.Split('/')[^1].Replace(".api", "");
            var assembly = Assembly.Load($"AppPlatform.{char.ToUpperInvariant(name[0])}{name[1..]}");

            var models = assembly.GetTypes()
                .Where(t => !t.IsAbstract && t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDesignTimeDbContextFactory<>)))
                .Select(factory =>
                {
                    dynamic instance = Activator.CreateInstance(factory)!;
                    return (IModel)((DbContext)instance.CreateDbContext(Array.Empty<string>())).Model;
                })
                .ToList();

            // A service contributing nothing would pass every check below by omission. Every service
            // has a design-time factory, because every service owns migrations.
            Assert.True(models.Count > 0, $"{service.ProjectDirectory} has no design-time DbContext factory.");
            return models;
        })];

    /// <summary>Qualified tables of IAppendOnly entities, except audit logs, which the scripts find by name.</summary>
    private static List<string> AppendOnlyTablesInCode()
        => [.. ServiceModels()
            .SelectMany(m => m.GetEntityTypes())
            .Where(e => typeof(IAppendOnly).IsAssignableFrom(e.ClrType) && e.GetTableName() != "audit_log")
            .Select(e => $"{e.GetSchema()}.{e.GetTableName()}")
            .Distinct()
            .Order()];

    /// <summary>
    /// The list the script actually executes: the `unnest(ARRAY[...]) AS listed` literal, found after
    /// stripping every comment from the script. Matching the raw text would be satisfied by a name
    /// left in a comment — `--` or `/* … */` — that PostgreSQL never runs.
    /// </summary>
    [GeneratedRegex(@"unnest\(ARRAY\[(?<list>[^\]]*)\]\)\s+AS\s+listed", RegexOptions.IgnoreCase)]
    private static partial Regex ListedArray();

    private static List<string> AppendOnlyTablesIn(string script)
    {
        var sql = WithoutComments(File.ReadAllText(Path.Combine(RepositoryPaths.Project("database/privileges"), script)));
        var match = ListedArray().Match(sql);
        Assert.True(match.Success, $"{script} has no `unnest(ARRAY[...]) AS listed` append-only list.");

        return [.. Regex.Matches(match.Groups["list"].Value, "'([^']+)'").Select(m => m.Groups[1].Value).Order()];
    }

    /// <summary>
    /// Removes `/* … */` and `--` comments. Adequate for these scripts, whose string literals hold no
    /// comment markers; not a general SQL parser.
    /// </summary>
    private static string WithoutComments(string sql)
        => Regex.Replace(Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline), "--[^\n]*", "");

    [Theory]
    [InlineData("unnest(ARRAY['ledger.a' /*, 'ledger.b' */]) AS listed", "ledger.a")]
    [InlineData("unnest(ARRAY[\n 'ledger.a' -- , 'ledger.b'\n]) AS listed", "ledger.a")]
    public void Commented_out_names_are_not_counted_as_listed(string sql, string only)
    {
        var match = ListedArray().Match(WithoutComments(sql));
        Assert.Equal([only], Regex.Matches(match.Groups["list"].Value, "'([^']+)'").Select(m => m.Groups[1].Value));
    }

    [Fact]
    public void Discovery_finds_the_ledger_journal()
        // Guards the guard: if discovery silently found nothing, the comparisons below would pass.
        => Assert.Equal(["ledger.journal_entry", "ledger.journal_line"], AppendOnlyTablesInCode());

    [Theory]
    [InlineData("02-grants.sql")]
    [InlineData("99-verify.sql")]
    public void The_script_lists_exactly_the_append_only_tables_in_code(string script)
        // Exactly, not "at least": a table listed in SQL but no longer append-only in code is the
        // same drift in the other direction, and just as misleading to the next reader.
        => Assert.Equal(AppendOnlyTablesInCode(), AppendOnlyTablesIn(script));
}
