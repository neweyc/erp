using AppPlatform.Boundary;

namespace AppPlatform.BoundaryTests;

public class SchemaBoundaryTests
{
    private static readonly string[] Published = ["core_v1", "identity_v1"];

    [Fact]
    public void An_app_mapping_its_own_schema_and_a_published_view_is_clean()
    {
        var model = Models.Of<GoodAppDbContext>();

        Assert.Empty(SchemaBoundary.FindEntitiesOutsideSchemas(model, "tickets", "core_v1", "identity_v1"));
        Assert.Empty(SchemaBoundary.FindWritableMappingsInPublishedSchemas(model, Published));
    }

    [Fact]
    public void Reaching_past_a_published_view_into_the_owning_schema_is_reported()
    {
        var problems = SchemaBoundary.FindEntitiesOutsideSchemas(
            Models.Of<BadAppDbContext>(), "tickets", "core_v1");

        Assert.Contains(problems, p =>
            p.Contains("SamplePublishedRow", StringComparison.Ordinal)
            && p.Contains("schema 'core'", StringComparison.Ordinal));
    }

    [Fact]
    public void Mapping_with_no_schema_is_reported()
    {
        // Left to the provider default this lands in `public`, outside every grant in
        // docs/database-privileges.md — so the boundary silently does not apply to it.
        var problems = SchemaBoundary.FindEntitiesOutsideSchemas(
            Models.Of<BadAppDbContext>(), "tickets", "core_v1");

        Assert.Contains(problems, p =>
            p.Contains("SampleRow", StringComparison.Ordinal)
            && p.Contains("default schema", StringComparison.Ordinal));
    }

    [Fact]
    public void A_published_view_mapped_as_a_writable_table_is_reported()
    {
        var problems = SchemaBoundary.FindWritableMappingsInPublishedSchemas(
            Models.Of<WritesPublishedViewDbContext>(), Published);

        Assert.Contains(problems, p =>
            p.Contains("SamplePublishedRow", StringComparison.Ordinal)
            && p.Contains("use ToView", StringComparison.Ordinal));
    }
}
