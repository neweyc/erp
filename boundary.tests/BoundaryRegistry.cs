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
            // core legitimately NAMES platform.tenant and platform.tenant_app: the published
            // session function joins both to resolve tenant status and licensed apps, and it
            // runs as ap_owner precisely because core's own role cannot read them. What core
            // must never touch is the operator's own world — accounts, sessions, audit, and the
            // commercial idempotency record.
            BannedSourceStrings: [
                "platform.audit_log", "platform.platform_user", "platform.platform_session",
                "platform.idempotency_record", "tickets."]),

        // platform.api sees the commercial record and nothing else. The banned strings are belt
        // to the grants' braces: it can neither NAME the tenant field key nor reach the schemas
        // it would decrypt. The source scan is also the only check that sees raw SQL, which the
        // EF model checks are blind to.
        new(
            ProjectDirectory: "platform.api",
            OwnSchemas: ["platform"],
            ReadableViewSchemas: [],
            ForbiddenAssemblyPrefixes: ["AppPlatform.Core", "AppPlatform.Tickets"],
            BannedSourceStrings: ["core.employee", "identity.user", "identity_v1.", "tickets."]),

        // The first licensed app, and the case the published-view design exists for: it reads
        // employees through core_v1 and has no grant on core.employee at all. The banned
        // strings catch the raw-SQL route that the EF model checks cannot see.
        new(
            ProjectDirectory: "apps/tickets/tickets.api",
            OwnSchemas: ["tickets"],
            ReadableViewSchemas: ["core_v1"],
            ForbiddenAssemblyPrefixes: ["AppPlatform.Core", "AppPlatform.Platform"],
            BannedSourceStrings: ["core.employee", "identity.", "platform."]),
    ];
}
