using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--why</c> answers "where does this package come from?" — and a diagnostic that answers the
/// wrong project, misses drift, or shrugs at a typo is worse than no answer. These tests pin the
/// classification (direct vs update-only vs transitive), the introducers read from the resolved
/// graph's own edges, the drift verdict over normalized versions, and the near-miss suggestions
/// that turn an unknown ID into something actionable.
/// </summary>
public class PackageOriginServiceTests
{
    private const string PackageId = "Serilog";

    [Fact]
    public void Analyze_DirectAndTransitiveMix_ClassifiesEachProject()
    {
        var request = BuildRequest(
            references:
            [
                Resolved("App", isTransitive: false),
                Resolved("Worker", isTransitive: true),
            ],
            declaredReferences: [Declared(projectName: "App")],
            graphs: [GraphWithIntroducer("Worker")]
        );

        var report = PackageOriginService.Analyze(request);
        report.Found.Should().BeTrue();
        report.Projects.Should().Contain(p =>
            p.ProjectName == "App.csproj" && p.Kind == PackageOriginKind.CentralPin
        );
        var worker = report.Projects.Should().Contain(p =>
            p.ProjectName == "Worker.csproj" && p.Kind == PackageOriginKind.TransitiveOnly
        ).Subject;
        worker.TransitiveIntroducers.Should().Equal("Newtonsoft.Json");
    }

    [Fact]
    public void Analyze_CentralPinOnly_ReportsResolvedVersionWithoutInlinePin()
    {
        var request = BuildRequest(
            references: [Resolved("App", version: "4.0.2", isTransitive: false)],
            declaredReferences:
            [
                new(
                    PackageId,
                    Version: "",
                    "/ws/src/App/App.csproj",
                    "App.csproj"
                ),
            ]
        );

        var report = PackageOriginService.Analyze(request);

        var app = report.Projects.Single(p => p.ProjectName == "App.csproj");
        app.Kind.Should().Be(PackageOriginKind.CentralPin);
        app.InlineVersion.Should().BeNull();
        // The central pin's value comes from restore, so the report carries it even though the
        // project file itself declares no version.
        app.ResolvedVersion.Should().Be("4.0.2");
    }

    [Fact]
    public void Analyze_InlineVersion_IsClassifiedAsSuch()
    {
        var request = BuildRequest(
            references: [Resolved("App", version: "3.1.0", isTransitive: false)],
            declaredReferences:
            [
                new(
                    PackageId,
                    Version: "3.1.0",
                    "/ws/src/App/App.csproj",
                    "App.csproj"
                ),
            ]
        );

        var report = PackageOriginService.Analyze(request);

        var app = report.Projects.Single(p => p.ProjectName == "App.csproj");
        app.Kind.Should().Be(PackageOriginKind.InlineVersion);
        app.InlineVersion.Should().Be("3.1.0");
    }

    [Fact]
    public void Analyze_UpdateOnlyDeclaration_DoesNotClaimDirectUse()
    {
        // An Update amends an inherited reference; it cannot put the package into this graph.
        var request = BuildRequest(
            references: [],
            declaredReferences:
            [
                new(PackageId, Version: "", "/ws/src/App/App.csproj", "App.csproj")
                {
                    IsMetadataOnlyUpdate = true,
                },
            ]
        );

        var report = PackageOriginService.Analyze(request);

        report.Projects.Single(p => p.ProjectName == "App.csproj")
            .Kind.Should()
            .Be(PackageOriginKind.UpdateOnly);
    }

    [Fact]
    public void Analyze_VersionDriftAcrossProjects_NamesEveryProjectAndVersion()
    {
        var request = BuildRequest(
            references:
            [
                Resolved("App", version: "4.0.2", isTransitive: false),
                Resolved("Worker", version: "3.1.0", isTransitive: false),
                Resolved("Tools", version: "4.0.2", isTransitive: true),
            ],
            declaredReferences: []
        );

        var report = PackageOriginService.Analyze(request);

        report.VersionsInUse.Should().HaveCount(2);
        report.VersionsInUse.Should().Contain(v =>
            v.Version == "4.0.2" && v.ProjectNames.Count == 2
        );
        report.VersionsInUse.Should().Contain(v =>
            v.Version == "3.1.0" && v.ProjectNames.Single() == "Worker.csproj"
        );
    }

    [Fact]
    public void Analyze_SameVersionDifferentSpellings_IsNotDrift()
    {
        var request = BuildRequest(
            references:
            [
                Resolved("App", version: "4.0.2", isTransitive: false),
                Resolved("Worker", version: "4.0.2.0", isTransitive: false),
            ],
            declaredReferences: []
        );

        var report = PackageOriginService.Analyze(request);

        // Two spellings of one release are not two versions; reporting drift here would cry wolf.
        report.VersionsInUse.Should().HaveCount(1);
    }

    [Fact]
    public void Analyze_UnknownPackage_IsNotFoundAndSuggestsCaseInsensitiveNearMisses()
    {
        var request = BuildRequest(
            references:
            [
                Resolved("App", isTransitive: false),
                OtherResolved("App", "Serilog.Sinks.Console"),
            ],
            declaredReferences: []
        );

        // A typo, in the wrong case: matching is case-insensitive so a correctly spelled ID is
        // found regardless, which means only a genuinely misspelled one lands here.
        var report = PackageOriginService.Analyze(request with { PackageId = "serilogg" });

        report.Found.Should().BeFalse();
        report.Suggestions.Should().Contain("Serilog");
    }

    [Fact]
    public void SuggestSimilar_TypoWithinEditDistance_IsSuggested()
    {
        var suggestions = PackageOriginService.SuggestSimilar(
            "Serilg",
            ["Serilog", "Polly", "Newtonsoft.Json"]
        );

        suggestions.Should().Contain("Serilog");
        suggestions.Should().NotContain("Newtonsoft.Json");
    }

    [Fact]
    public void SuggestSimilar_LongNameContainment_IsSuggestedEitherDirection()
    {
        PackageOriginService.SuggestSimilar(
            "Microsoft.Extensions",
            ["Microsoft.Extensions.Logging"]
        ).Should().Contain("Microsoft.Extensions.Logging");

        PackageOriginService.SuggestSimilar(
            "Extensions.Logging.Abstractions",
            ["Microsoft.Extensions.Logging.Abstractions"]
        ).Should().Contain("Microsoft.Extensions.Logging.Abstractions");
    }

    [Fact]
    public void SuggestSimilar_NothingClose_ReturnsEmptyRatherThanGuessing()
    {
        PackageOriginService.SuggestSimilar("Zzzqqq", ["Serilog", "Polly"])
            .Should()
            .BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Builders
    // ─────────────────────────────────────────────────────────────────────────

    private static string ProjectPath(string projectName) => $"/ws/src/{projectName}/{projectName}.csproj";

    private static PackageReference Resolved(
        string projectName,
        string version = "4.0.2",
        bool isTransitive = false
    ) =>
        new(PackageId, version, ProjectPath(projectName), projectName, isTransitive);

    private static PackageReference OtherResolved(string projectName, string packageName) =>
        new(packageName, "1.0.0", ProjectPath(projectName), projectName);

    private static PackageReference Declared(string projectName) =>
        new(PackageId, "", ProjectPath(projectName), projectName);

    /// <summary>
    /// A graph where <c>Newtonsoft.Json</c> directly depends on the traced package — the shape that
    /// lets the service name an introducer instead of saying "unknown".
    /// </summary>
    private static ProjectResolvedGraph GraphWithIntroducer(string projectName)
    {
        var path = ProjectPath(projectName);
        return new ProjectResolvedGraph(
            path,
            [
                new(
                    "net8.0",
                    Resolved: true,
                    [
                        new("Newtonsoft.Json", "13.0.3", IsDirect: true, ["Serilog"]),
                        new(PackageId, "4.0.2", IsDirect: false),
                    ]
                ),
            ]
        );
    }

    private static PackageOriginRequest BuildRequest(
        IReadOnlyList<PackageReference> references,
        IReadOnlyList<PackageReference> declaredReferences,
        IReadOnlyList<ProjectResolvedGraph>? graphs = null
    ) =>
        new(
            PackageId,
            new ProjectPackageInfo(references, DeclaredReferences: declaredReferences),
            graphs ?? [],
            ProjectCount: 0,
            FailedScanCount: 0
        );
}
