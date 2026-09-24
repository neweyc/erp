using AppPlatform.Boundary;

namespace AppPlatform.BoundaryTests;

public class SourceScanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("boundary-scan").FullName;

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_banned_string_is_reported_with_file_and_line()
    {
        Write("Feature.cs", "var x = 1;\nvar key = config[\"Encryption:FieldKey\"];\n");

        var hits = SourceScan.FindBannedStrings(_root, ["Encryption:FieldKey"]);

        Assert.Equal(["Feature.cs:2: Encryption:FieldKey"], hits);
    }

    [Fact]
    public void Raw_sql_naming_a_forbidden_schema_is_caught()
    {
        // The EF model checks are blind to this: the string never becomes an entity.
        Write("Report.cs", "db.Database.SqlQuery<int>($\"select count(*) from core.employee\");");

        Assert.NotEmpty(SourceScan.FindBannedStrings(_root, ["core.employee"]));
    }

    [Fact]
    public void Build_output_is_not_scanned()
    {
        // Generated and copied files under bin/obj would otherwise report the banned
        // string of every project that ever built here.
        Write("obj/Generated.cs", "Encryption:FieldKey");
        Write("bin/Debug/Copied.cs", "Encryption:FieldKey");

        Assert.Empty(SourceScan.FindBannedStrings(_root, ["Encryption:FieldKey"]));
    }

    [Fact]
    public void Only_the_requested_extensions_are_scanned()
    {
        Write("notes.md", "Encryption:FieldKey is documented here, which is fine");

        Assert.Empty(SourceScan.FindBannedStrings(_root, ["Encryption:FieldKey"]));
        Assert.NotEmpty(SourceScan.FindBannedStrings(_root, ["Encryption:FieldKey"], ".md"));
    }

    [Fact]
    public void Scanning_a_missing_directory_throws_rather_than_passing()
    {
        // A scan pointed at nothing returns no hits, and "no hits" gets read as evidence
        // that the rule holds.
        Assert.Throws<DirectoryNotFoundException>(() =>
            SourceScan.FindBannedStrings(Path.Combine(_root, "not-here"), ["anything"]));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
