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
    /// Empty until the first service lands in Milestone 1. The shape each entry will take:
    ///
    ///   new("platform.api", ["platform"], [], ["AppPlatform.Core"], ["Encryption:FieldKey"])
    ///   new("core.api",     ["core", "identity"], [], [], [])
    ///   new("apps/tickets/tickets.api", ["tickets"], ["core_v1", "identity_v1"],
    ///       ["AppPlatform.Platform"], ["core.employee", "identity."])
    ///
    /// platform.api banning "Encryption:FieldKey" in source is the belt to the grants'
    /// braces: it can neither name the key nor read the schemas it would decrypt.
    /// </summary>
    public static readonly ServiceBoundary[] Services = [];
}
