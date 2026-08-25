using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--why</c> answers "where does this package come from?" — and a diagnostic that answers the
/// wrong project, misses drift, or shrugs at a typo is worse than no answer. These tests pin the
/// classification (direct vs inherited vs update-only vs transitive), the introducers read from
/// the resolved graph's own edges, the drift verdict over normalized versions — including within a
/// single multi-targeted project — the visibility of projects that do not have the package at all,
/// and the near-miss suggestions that turn an unknown ID into something actionable.
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
            declaredReferences: [Declared("App")],
            graphs: [GraphWithIntroducer("Worker")]
        );

        var report = PackageOriginService.Analyze(request);

        report.Found.Should().BeTrue();
        report.Projects.Should().Contain(p =>
            p.DisplayPath == "src/App/App.csproj" && p.Kind == PackageOriginKind.CentralPin
        );
        var worker = report.Projects.Should().Contain(p =>
            p.DisplayPath == "src/Worker/Worker.csproj"
            && p.Kind == PackageOriginKind.TransitiveOnly
        ).Subject;
        worker.TransitiveIntroducers.Should().Equal("Newtonsoft.Json");
    }

    [Fact]
    public void Analyze_CentralPinOnly_ReportsResolvedVersionWithoutInlinePin()
    {
        var request = BuildRequest(
            references: [Resolved("App", version: "4.0.2", isTransitive: false)],
            declaredReferences: [Declared("App")]
        );

        var report = PackageOriginService.Analyze(request);

        var app = report.Projects.Single(p => p.DisplayPath == "src/App/App.csproj");
        app.Kind.Should().Be(PackageOriginKind.CentralPin);
        app.InlineVersion.Should().BeNull();
        // The central pin's value comes from restore, so the report carries it even though the
        // project file itself declares no version.
        app.ResolvedVersions.Should().Equal(["4.0.2"]);
    }

    [Fact]
    public void Analyze_InlineVersion_IsClassifiedAsSuch()
    {
        var request = BuildRequest(
            references: [Resolved("App", version: "3.1.0", isTransitive: false)],
            declaredReferences:
            [
                new(PackageId, Version: "3.1.0", "/ws/src/App/App.csproj", "App"),
            ]
        );

        var report = PackageOriginService.Analyze(request);

        var app = report.Projects.Single(p => p.DisplayPath == "src/App/App.csproj");
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
                new(PackageId, Version: "", "/ws/src/App/App.csproj", "App")
                {
                    IsMetadataOnlyUpdate = true,
                },
            ]
        );

        var report = PackageOriginService.Analyze(request);

        report.Projects.Single(p => p.DisplayPath == "src/App/App.csproj")
            .Kind.Should()
            .Be(PackageOriginKind.UpdateOnly);
    }

    [Fact]
    public void Analyze_ResolvableTopLevelButNeverDeclared_IsInheritedRatherThanTransitive()
    {
        // A resolved row marked non-transitive with no local declaration comes from an import —
        // Directory.Build.props, an SDK, a targets file. Calling it transitive would send someone
        // hunting for a referencing package that does not exist.
        var request = BuildRequest(
            references: [Resolved("App", version: "8.0.0", isTransitive: false)],
            declaredReferences: []
        );

        var report = PackageOriginService.Analyze(request);

        var app = report.Projects.Single(p => p.DisplayPath == "src/App/App.csproj");
        app.Kind.Should().Be(PackageOriginKind.Inherited);
        app.TransitiveIntroducers.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_MultiTargetedProjectWithDifferentVersions_RetainsEveryVersion()
    {
        // Two frameworks resolving to different versions of one package is real intra-project
        // drift; keeping only the first row would make the verdict depend on scan order.
        var request = BuildRequest(
            references:
            [
                Resolved("App", version: "3.1.1", isTransitive: false),
                Resolved("App", version: "4.0.2", isTransitive: false),
            ],
            declaredReferences: [Declared("App")]
        );

        var report = PackageOriginService.Analyze(request);

        var app = report.Projects.Single(p => p.DisplayPath == "src/App/App.csproj");
        app.ResolvedVersions.Should().BeEquivalentTo(["3.1.1", "4.0.2"]);

        // Both versions are reported, and the one project appears under each.
        report.VersionsInUse.Should().HaveCount(2);
        report.VersionsInUse.Should().Contain(v =>
            v.Version == "3.1.1" && v.Projects.Single() == "src/App/App.csproj"
        );
        report.VersionsInUse.Should().Contain(v =>
            v.Version == "4.0.2" && v.Projects.Single() == "src/App/App.csproj"
        );
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
            v.Version == "4.0.2" && v.Projects.Count == 2
        );
        report.VersionsInUse.Should().Contain(v =>
            v.Version == "3.1.0" && v.Projects.Single() == "src/Worker/Worker.csproj"
        );
    }

    [Fact]
    public void Analyze_ProjectWithoutThePackage_IsStillReportedAsNotPresent()
    {
        // A project that declares and resolves nothing for this package is not allowed to vanish:
        // "which projects don't have it" is half of where-does-it-come-from.
        var request = BuildRequest(
            references: [Resolved("App", isTransitive: false)],
            declaredReferences: [Declared("App")],
            projectPaths:
            [
                "/ws/src/App/App.csproj",
                "/ws/src/Empty/Empty.csproj",
            ]
        );

        var report = PackageOriginService.Analyze(request);

        report.Projects.Select(p => p.DisplayPath)
            .Should()
            .Contain("src/Empty/Empty.csproj");
        report.Projects.Single(p => p.DisplayPath == "src/Empty/Empty.csproj")
            .Kind.Should()
            .Be(PackageOriginKind.NotPresent);
    }

    [Fact]
    public void Analyze_DuplicateProjectFileNames_AreDisambiguatedByRelativePath()
    {
        var request = BuildRequest(
            references:
            [
                Resolved("App", version: "4.0.2", isTransitive: false),
                new(
                    PackageId,
                    "3.1.1",
                    "/ws/tests/App/App.csproj",
                    "App",
                    IsTransitive: true
                ),
            ],
            declaredReferences: [Declared("App")],
            projectPaths:
            [
                "/ws/src/App/App.csproj",
                "/ws/tests/App/App.csproj",
            ]
        );

        var report = PackageOriginService.Analyze(request);

        // Two App.csproj files must remain distinguishable everywhere they appear.
        report.VersionsInUse.Should().Contain(v =>
            v.Version == "4.0.2" && v.Projects.Single() == "src/App/App.csproj"
        );
        report.VersionsInUse.Should().Contain(v =>
            v.Version == "3.1.1" && v.Projects.Single() == "tests/App/App.csproj"
        );
    }

    [Fact]
    public async Task RunAsync_PackageAbsentButScansFailed_ReturnsIncompleteRatherThanNotFound()
    {
        // Absence proven only over the projects that happened to scan is not absence: the package
        // may live in an unread project. Exit 1 would tell a script to stop looking.
        var request = BuildRequest(
            references: [],
            declaredReferences: [],
            failedScanCount: 1,
            projectCount: 2
        );
        var console = new CPMigrate.Tests.TestDoubles.FakeConsoleService();
        var service = new PackageOriginService(console);

        var exitCode = await service.RunAsync(request);

        exitCode.Should().Be(ExitCodes.IncompleteAnalysis);
        console.ErrorMessages.Should().Contain(m => m.Contains("could not be read"));
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

    private static string ProjectPath(string projectName) =>
        $"/ws/src/{projectName}/{projectName}.csproj";


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

    [Fact]
    public void Analyze_ProjectWithFailedScans_IsUnreadableRatherThanNotPresent()
    {
        var request = BuildRequest(
            references: [],
            declaredReferences: [],
            projectPaths: ["/ws/src/App/App.csproj", "/ws/src/Worker/Worker.csproj"],
            scanOutcomes:
            [
                new("/ws/src/App/App.csproj", ResolvedRead: false, DeclarationsRead: false),
                new("/ws/src/Worker/Worker.csproj", ResolvedRead: true, DeclarationsRead: true),
            ]
        );

        var report = PackageOriginService.Analyze(request);

        var app = report.Projects.Single(p => p.DisplayPath == "src/App/App.csproj");
        // A project nobody could read has not been checked, so "not present" would be an
        // assertion about data the scan never saw.
        app.Kind.Should().Be(PackageOriginKind.Unreadable);
        app.ResolvedVersions.Should().BeNull();
    }

    [Fact]
    public void Analyze_PartialLegFailure_IsAlsoUnreadable()
    {
        var request = BuildRequest(
            references: [],
            declaredReferences: [],
            projectPaths: ["/ws/src/App/App.csproj"],
            scanOutcomes:
            [
                new("/ws/src/App/App.csproj", ResolvedRead: true, DeclarationsRead: false),
            ]
        );

        var report = PackageOriginService.Analyze(request);

        report.Projects.Single().Kind.Should().Be(PackageOriginKind.Unreadable);
    }

    [Fact]
    public async Task RunManyAsync_RendersEachPackageFromTheSharedScan_AndNamesItInABanner()
    {
        // One scanned dataset serves both questions: Serilog is found, Serilogg is not. The
        // console run must answer both, each under its own named banner.
        var found = BuildRequest(references: [Resolved("App", isTransitive: false)], declaredReferences: [Declared("App")]);
        var missing = found with { PackageId = "Serilogg" };
        var console = new CPMigrate.Tests.TestDoubles.FakeConsoleService();

        var exitCode = await new PackageOriginService(console).RunManyAsync([found, missing]);

        console.BannerMessages.Should().Contain(m => m.Contains("PACKAGE ORIGIN — Serilog"));
        console.BannerMessages.Should().Contain(m => m.Contains("PACKAGE ORIGIN — Serilogg"));
        // The not-found prose names its own package: both answers rendered from one dataset.
        console.ErrorMessages.Should().ContainSingle(m => m.Contains("'Serilogg'"));
        exitCode.Should().Be(ExitCodes.ValidationError);
    }

    [Fact]
    public async Task RunManyAsync_ComposesExitCodesByWorstOutcome()
    {
        var shared = BuildRequest(
            references: [],
            declaredReferences: [],
            projectPaths: ["/ws/src/App/App.csproj"],
            failedScanCount: 1,
            projectCount: 1
        );
        var incomplete = shared with { PackageId = "A" };
        var notFound = shared with { PackageId = "B", FailedScanCount = 0 };
        var console = new CPMigrate.Tests.TestDoubles.FakeConsoleService();

        var exitCode = await new PackageOriginService(console).RunManyAsync([notFound, incomplete]);

        // Incomplete beats not-found: a script reading only the process code must not mistake
        // "one package absent" for "the whole workspace was read".
        exitCode.Should().Be(ExitCodes.IncompleteAnalysis);
    }

    [Theory]
    [InlineData(new[] { 0, 0 }, 0)]
    [InlineData(new[] { 1, 0 }, 1)]
    [InlineData(new[] { 0, 8 }, 8)]
    [InlineData(new[] { 1, 8 }, 8)]
    [InlineData(new[] { 4 }, 4)]
    public void CombineExitCodes_WorstOutcomeWins(int[] codes, int expected)
    {
        PackageOriginService.CombineExitCodes(codes).Should().Be(expected);
    }

    [Fact]
    public void Analyze_OnlyUnreadableProjects_AreNotReportedAsFound()
    {
        var request = BuildRequest(
            references: [],
            declaredReferences: [],
            projectPaths: ["/ws/src/App/App.csproj"],
            scanOutcomes:
            [
                new("/ws/src/App/App.csproj", ResolvedRead: false, DeclarationsRead: false),
            ]
        );

        var report = PackageOriginService.Analyze(request);

        report.Found.Should().BeFalse();
    }

    private static PackageOriginRequest BuildRequest(
        IReadOnlyList<PackageReference> references,
        IReadOnlyList<PackageReference> declaredReferences,
        IReadOnlyList<ProjectResolvedGraph>? graphs = null,
        IReadOnlyList<string>? projectPaths = null,
        int failedScanCount = 0,
        int projectCount = 0,
        IReadOnlyList<PackageOriginProjectScan>? scanOutcomes = null
    ) =>
        new(
            PackageId,
            new ProjectPackageInfo(references, BasePath: "/ws", DeclaredReferences: declaredReferences),
            graphs ?? [],
            projectCount,
            failedScanCount,
            projectPaths,
            scanOutcomes
        );
}
