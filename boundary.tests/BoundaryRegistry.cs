namespace AppPlatform.BoundaryTests;

/// <summary>
/// What each deployable is allowed to touch. One entry per service; adding a service to
/// the solution without an entry fails <see cref="ServiceCoverageTests"/>, which is what
/// stops the boundary suite quietly falling behind the code.
/// </summary>
/// <param name="ProjectDirectory">Repo-relative, used for the source scan.</param>
/// <param name="OwnSchemas">Schemas this service owns and may write.</param>
/// <param name="ReadableViewSchemas">Published contracts it may read, never write.</param>
/// <param name="ForbiddenAssemblyPrefixes">Assemblies it must not reference, directly or transitively.</param>
/// <param name="BannedSourceStrings">Text its source must never contain.</param>
public sealed record ServiceBoundary(
    string ProjectDirectory,
    string[] OwnSchemas,
    string[] ReadableViewSchemas,
    string[] ForbiddenAssemblyPrefixes,
    string[] BannedSourceStrings);

public static class BoundaryRegistry
{
    /// <summary>Published read contracts. Writable mappings to these are always a violation.</summary>
    public static readonly string[] PublishedSchemas = ["core_v1", "identity_v1"];

    /// <summary>
    /// One entry per deployable. Adding a service to the solution without one fails
    /// <see cref="ServiceCoverageTests"/>.
    /// </summary>
    public static readonly ServiceBoundary[] Services =
    [
        // core.api owns core and identity. `platform` appears because of the ONE deliberate
        // exception in the design: the tenant row, its company, and the admin invite must
        // commit in a single transaction, and a transaction cannot span two services. At the
        // database that exception is SELECT + INSERT on platform.tenant only — no UPDATE, and
        // nothing else in the schema. Core can bring a tenant into existence; only the
        // operator can change what it is permitted to do.
        new(
            ProjectDirectory: "core.api",
            OwnSchemas: ["core", "identity", "platform"],
            ReadableViewSchemas: [],
            ForbiddenAssemblyPrefixes: ["AppPlatform.Platform", "AppPlatform.Tickets"],
            BannedSourceStrings: []),
    ];
}
