using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--tree --output Json</c> promises CI scripts one parseable document that says what the
/// console tree says: every discovered project's direct and transitive packages, with a
/// <c>scanned</c> flag separating "declared nothing" from "could not be read" — the distinction
/// the console only reports as warnings.
/// </summary>
public class DependencyTreeJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Parse(Serialize(
            references: [Direct("App", "Newtonsoft.Json", "13.0.1")],
            projects: [("/ws/src/App/App.csproj", true)]
        ));

        root.GetProperty("operation").GetString().Should().Be("tree");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void Serialize_SplitsDirectFromTransitiveInNameOrder()
    {
        var root = Parse(Serialize(
            references:
            [
                Direct("App", "Zebra", "1.0.0"),
                Transitive("App", "Alpha", "2.0.0"),
                Direct("App", "Apple", "3.0.0"),
                Transitive("App", "Zulu", "4.0.0"),
            ],
            projects: [("/ws/src/App/App.csproj", true)]
        ));

        var app = root.GetProperty("projects").EnumerateArray().Single();
        app.GetProperty("direct").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .Should().Equal("Apple", "Zebra");
        app.GetProperty("transitive").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .Should().Equal("Alpha", "Zulu");
    }

    [Fact]
    public void Serialize_CentralPin_EmitsNoVersion()
    {
        // Under CPM a declaration carries no version — the pin lives in Directory.Packages.props —
        // and the field is absent rather than an empty string a consumer has to special-case.
        var root = Parse(Serialize(
            references: [Direct("App", "Newtonsoft.Json", "")],
            projects: [("/ws/src/App/App.csproj", true)]
        ));

        var package = root.GetProperty("projects")[0].GetProperty("direct")[0];
        package.GetProperty("name").GetString().Should().Be("Newtonsoft.Json");
        package.TryGetProperty("version", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_FailedScan_MarksTheProjectUnread()
    {
        // A project that produced no rows because its scan failed is not a project with no
        // packages — the flag is what keeps a consumer from reading the first as the second.
        var root = Parse(Serialize(
            references: [Direct("App", "Newtonsoft.Json", "13.0.1")],
            projects: [("/ws/src/App/App.csproj", true), ("/ws/src/Broken/Broken.csproj", false)]
        ));

        var broken = root.GetProperty("projects").EnumerateArray()
            .Single(p => p.GetProperty("projectPath").GetString()!.Contains("Broken"));
        broken.GetProperty("scanned").GetBoolean().Should().BeFalse();
        broken.GetProperty("direct").GetArrayLength().Should().Be(0);
        root.GetProperty("summary").GetProperty("failedScans").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Serialize_CleanProjectWithNoPackages_IsScannedAndEmpty()
    {
        // The other half of the flag's contract: a project that scanned fine and declares nothing
        // reports scanned: true with empty lists, not scanned: false.
        var root = Parse(Serialize(
            references: [],
            projects: [("/ws/src/Empty/Empty.csproj", true)]
        ));

        var empty = root.GetProperty("projects").EnumerateArray().Single();
        empty.GetProperty("scanned").GetBoolean().Should().BeTrue();
        empty.GetProperty("direct").GetArrayLength().Should().Be(0);
        empty.GetProperty("transitive").GetArrayLength().Should().Be(0);
        root.GetProperty("summary").GetProperty("failedScans").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Serialize_ProjectsAreOrderedByPathWithRelativeIds()
    {
        var root = Parse(Serialize(
            references:
            [
                Direct("B", "Beta", "1.0.0"),
                Direct("A", "Alpha", "1.0.0"),
            ],
            projects: [("/ws/src/B/B.csproj", true), ("/ws/src/A/A.csproj", true)]
        ));

        var projects = root.GetProperty("projects").EnumerateArray().ToList();
        projects.Select(p => p.GetProperty("relativePath").GetString())
            .Should().Equal("src/A/A.csproj", "src/B/B.csproj");
    }

    [Fact]
    public void Serialize_SummaryCountsEveryReference()
    {
        var root = Parse(Serialize(
            references:
            [
                Direct("App", "Newtonsoft.Json", "13.0.1"),
                Transitive("App", "Serilog", "4.0.0"),
                Transitive("App", "Polly", "8.0.0"),
            ],
            projects: [("/ws/src/App/App.csproj", true)]
        ));

        var summary = root.GetProperty("summary");
        summary.GetProperty("projectCount").GetInt32().Should().Be(1);
        summary.GetProperty("direct").GetInt32().Should().Be(1);
        summary.GetProperty("transitive").GetInt32().Should().Be(2);
    }

    private static string Serialize(
        IReadOnlyList<PackageReference> references,
        IReadOnlyList<(string Path, bool Scanned)> projects
    )
    {
        var packageInfo = new ProjectPackageInfo(references, BasePath: "/ws");
        return DependencyTreeJsonWriter.Serialize(packageInfo, projects, ExitCodes.Success);
    }

    private static PackageReference Direct(string project, string package, string version) =>
        new(package, version, $"/ws/src/{project}/{project}.csproj", project, IsTransitive: false);

    private static PackageReference Transitive(string project, string package, string version) =>
        new(package, version, $"/ws/src/{project}/{project}.csproj", project, IsTransitive: true);

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
