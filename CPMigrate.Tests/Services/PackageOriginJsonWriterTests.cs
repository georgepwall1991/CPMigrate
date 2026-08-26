using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--why --output Json</c> promises CI scripts one parseable document that says what the console
/// tree says: per-project kind, versions (including per target framework), transitive introducers,
/// drift, and the exit code mirrored inside the document. These tests pin that document's shape for
/// every relationship kind, and pin the exit codes to be identical to the console path's — a mode
/// whose verdict depended on how it rendered would be worse than none.
/// </summary>
public class PackageOriginJsonWriterTests
{
    private const string PackageId = "Serilog";

    [Fact]
    public void Serialize_DirectDeclaration_EmitsFoundStatusAndCentralPinKind()
    {
        var root = Parse(Serialize(BuildRequest(
            references: [Resolved("App", isTransitive: false)],
            declaredReferences: [Declared("App")]
        )));

        root.GetProperty("operation").GetString().Should().Be("why");
        root.GetProperty("packageId").GetString().Should().Be(PackageId);
        root.GetProperty("status").GetString().Should().Be("found");
        root.GetProperty("outputSchemaVersion").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();

        var app = Project(root, "src/App/App.csproj");
        app.GetProperty("kind").GetString().Should().Be("centralPin");
        app.GetProperty("projectPath").GetString().Should().Be("/ws/src/App/App.csproj");
        app.GetProperty("resolvedVersions").EnumerateArray()
            .Select(v => v.GetString())
            .Should()
            .Equal("4.0.2");

        var summary = root.GetProperty("summary");
        summary.GetProperty("direct").GetInt32().Should().Be(1);
        summary.GetProperty("transitive").GetInt32().Should().Be(0);
        summary.GetProperty("failedScans").GetInt32().Should().Be(0);

        root.GetProperty("versionsInUse").EnumerateArray()
            .Should()
            .Contain(v =>
                v.GetProperty("version").GetString() == "4.0.2"
                && v.GetProperty("projects").EnumerateArray()
                    .Any(p => p.GetString() == "src/App/App.csproj")
            );
    }

    [Fact]
    public void Serialize_InlineVersion_CarriesThePinnedVersion()
    {
        var root = Parse(Serialize(BuildRequest(
            references: [Resolved("App", version: "3.1.0", isTransitive: false)],
            declaredReferences:
            [
                new(PackageId, Version: "3.1.0", "/ws/src/App/App.csproj", "App"),
            ]
        )));

        var app = Project(root, "src/App/App.csproj");
        app.GetProperty("kind").GetString().Should().Be("inlineVersion");
        app.GetProperty("inlineVersion").GetString().Should().Be("3.1.0");
    }

    [Fact]
    public void Serialize_InheritedProject_HasNoIntroducersToName()
    {
        // Top-level in the resolved graph with no local declaration means the declaration lives in
        // an import; there is no direct package pulling it in, so the field must be absent rather
        // than an empty list that reads as "checked, nobody".
        var root = Parse(Serialize(BuildRequest(
            references: [Resolved("App", version: "8.0.0", isTransitive: false)],
            declaredReferences: []
        )));

        var app = Project(root, "src/App/App.csproj");
        app.GetProperty("kind").GetString().Should().Be("inherited");
        app.TryGetProperty("transitiveIntroducers", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_TransitiveOnly_NamesTheIntroducingDirectPackage()
    {
        var root = Parse(Serialize(BuildRequest(
            references: [Resolved("Worker", isTransitive: true)],
            declaredReferences: [],
            graphs: [GraphWithIntroducer("Worker")],
            projectPaths: [ProjectPath("Worker")]
        )));

        var worker = Project(root, "src/Worker/Worker.csproj");
        worker.GetProperty("kind").GetString().Should().Be("transitiveOnly");
        worker.GetProperty("transitiveIntroducers").EnumerateArray()
            .Select(i => i.GetString())
            .Should()
            .Contain("Newtonsoft.Json");

        root.GetProperty("summary").GetProperty("transitive").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Serialize_ProjectWithoutThePackage_IsReportedAsNotPresent()
    {
        // "Which projects don't have it" is half of where-does-it-come-from, so an absent project
        // must appear in the document, not vanish between the entries that do name it.
        var root = Parse(Serialize(BuildRequest(
            references: [Resolved("App", isTransitive: false)],
            declaredReferences: [Declared("App")],
            projectPaths:
            [
                "/ws/src/App/App.csproj",
                "/ws/src/Empty/Empty.csproj",
            ]
        )));

        var empty = Project(root, "src/Empty/Empty.csproj");
        empty.GetProperty("kind").GetString().Should().Be("notPresent");
        // A scanned project asserts "no versions here"; only an unreadable one stays silent.
        empty.GetProperty("resolvedVersions").EnumerateArray().Should().BeEmpty();
        empty.TryGetProperty("versionsByTargetFramework", out _).Should().BeFalse();
        root.GetProperty("summary").GetProperty("notPresent").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Serialize_UnreadableProject_AssertsNothingAboutVersions()
    {
        var request = BuildRequest(
            references: [],
            declaredReferences: [],
            projectPaths: ["/ws/src/App/App.csproj"],
            scanOutcomes:
            [
                new("/ws/src/App/App.csproj", ResolvedRead: false, DeclarationsRead: false),
            ],
            failedScanCount: 1
        );

        var root = Parse(Serialize(request));
        var app = Project(root, "src/App/App.csproj");
        app.GetProperty("kind").GetString().Should().Be("unreadable");
        app.TryGetProperty("resolvedVersions", out _).Should().BeFalse();
        app.TryGetProperty("versionsByTargetFramework", out _).Should().BeFalse();

        root.GetProperty("summary").GetProperty("unreadable").GetInt32().Should().Be(1);
        root.GetProperty("summary").GetProperty("failedScans").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Serialize_ReadableGraph_EmitsOneVersionPerTargetFramework()
    {
        var graph = new ProjectResolvedGraph(
            "/ws/src/App/App.csproj",
            [
                new("net6.0", Resolved: true, [new(PackageId, "3.1.1", IsDirect: true)]),
                new("net8.0", Resolved: true, [new(PackageId, "4.0.2", IsDirect: true)]),
                // A framework restore never described contributes nothing.
                new("net9.0", Resolved: false, []),
            ]
        );

        var root = Parse(Serialize(BuildRequest(
            references:
            [
                Resolved("App", version: "3.1.1", isTransitive: false),
                Resolved("App", version: "4.0.2", isTransitive: false),
            ],
            declaredReferences: [Declared("App")],
            graphs: [graph],
            projectPaths: [ProjectPath("App")],
            // Per-framework versions are only claimed for scans that actually read the project.
            scanOutcomes:
            [
                new(ProjectPath("App"), ResolvedRead: true, DeclarationsRead: true),
            ]
        )));
        var frameworks = Project(root, "src/App/App.csproj")
            .GetProperty("versionsByTargetFramework");
        frameworks.GetArrayLength().Should().Be(2);
        frameworks.EnumerateArray()
            .Should()
            .Contain(f =>
                f.GetProperty("targetFramework").GetString() == "net6.0"
                && f.GetProperty("version").GetString() == "3.1.1"
            )
            .And.Contain(f =>
                f.GetProperty("targetFramework").GetString() == "net8.0"
                && f.GetProperty("version").GetString() == "4.0.2"
            );
    }

    [Fact]
    public async Task Serialize_NotFoundWithoutGraphs_StatusIsNotFoundWithSuggestionsAndExitCode()
    {
        var request = BuildRequest(
            references:
            [
                Resolved("App", isTransitive: false),
                OtherResolved("App", "Serilog.Sinks.Console"),
            ],
            declaredReferences: []
        ) with { PackageId = "serilogg" };

        var consoleExitCode = await ConsoleRunAsync(request);
        var root = Parse(Serialize(request));

        root.GetProperty("status").GetString().Should().Be("not-found");
        root.GetProperty("suggestions").EnumerateArray()
            .Select(s => s.GetString())
            .Should()
            .Contain("Serilog");
        root.GetProperty("versionsInUse").EnumerateArray().Should().BeEmpty();

        // The document mirrors the process verdict, so a consumer parsing stdout alone gets the
        // same answer a shell script reading $? gets.
        root.GetProperty("exitCode")
            .GetInt32()
            .Should()
            .Be(ExitCodes.ValidationError)
            .And.Be(consoleExitCode);
    }

    [Theory]
    [InlineData(false, ExitCodes.Success)]
    [InlineData(true, ExitCodes.IncompleteAnalysis)]
    public async Task ExitCode_FoundPackage_MatchesWhatRunAsyncReturns(bool failedScans, int expected)
    {
        var request = BuildRequest(
            references: [Resolved("App", isTransitive: false)],
            declaredReferences: [Declared("App")],
            projectPaths: ["/ws/src/App/App.csproj"],
            failedScanCount: failedScans ? 1 : 0
        );

        await AssertParity(request, expected);
    }

    [Theory]
    [InlineData(false, ExitCodes.ValidationError)]
    [InlineData(true, ExitCodes.IncompleteAnalysis)]
    public async Task ExitCode_MissingPackage_MatchesWhatRunAsyncReturns(bool failedScans, int expected)
    {
        // Absence proven only over half the workspace is not absence — the console path exits
        // incomplete rather than not-found there, and the document must say the same thing.
        var request = BuildRequest(
            references: [],
            declaredReferences: [],
            projectPaths: ["/ws/src/App/App.csproj"],
            failedScanCount: failedScans ? 1 : 0
        );

        await AssertParity(request, expected);
    }

    [Fact]
    public void SerializeMany_FoundAndNotFoundMix_EmitsWhyManyDocumentWithPerPackageAnswers()
    {
        var found = BuildRequest(
            references: [Resolved("App", isTransitive: false)],
            declaredReferences: [Declared("App")]
        );
        var missing = BuildRequest(
            references:
            [
                Resolved("App", isTransitive: false),
                OtherResolved("App", "Serilog.Sinks.Console"),
            ],
            declaredReferences: []
        ) with { PackageId = "serilogg" };

        var root = Parse(SerializeMany(found, missing));

        root.GetProperty("operation").GetString().Should().Be("why-many");
        root.GetProperty("outputSchemaVersion").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("packageIds").EnumerateArray()
            .Select(id => id.GetString())
            .Should()
            .Equal("Serilog", "serilogg");

        // Answers come back in the order the IDs were passed, each shaped like its
        // single-package counterpart minus the document-level fields.
        var results = root.GetProperty("results").EnumerateArray().ToList();
        results.Should().HaveCount(2);

        var foundAnswer = results[0];
        foundAnswer.GetProperty("packageId").GetString().Should().Be("Serilog");
        foundAnswer.GetProperty("status").GetString().Should().Be("found");
        foundAnswer.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        Project(foundAnswer, "src/App/App.csproj").GetProperty("kind").GetString()
            .Should().Be("centralPin");
        foundAnswer.GetProperty("summary").GetProperty("direct").GetInt32().Should().Be(1);
        foundAnswer.GetProperty("summary").GetProperty("projectCount").GetInt32().Should().Be(2);

        var missingAnswer = results[1];
        missingAnswer.GetProperty("packageId").GetString().Should().Be("serilogg");
        missingAnswer.GetProperty("status").GetString().Should().Be("not-found");
        missingAnswer.GetProperty("exitCode").GetInt32().Should()
            .Be(ExitCodes.ValidationError);
        missingAnswer.GetProperty("suggestions").EnumerateArray()
            .Select(s => s.GetString())
            .Should()
            .Contain("Serilog");
    }

    [Fact]
    public void SerializeMany_DocumentExitCode_IsTheWorstOfThePerPackageAnswers()
    {
        // One found over a fully-read workspace (0) plus one not-found (1): the process exits
        // 1, exactly what RunManyAsync would return for the same run.
        var found = BuildRequest(
            references: [Resolved("App", isTransitive: false)],
            declaredReferences: [Declared("App")],
            projectPaths: [ProjectPath("App")]
        );
        var missing = BuildRequest(
            references: [],
            declaredReferences: [],
            projectPaths: [ProjectPath("App")]
        ) with { PackageId = "Unknown.Package" };

        var root = Parse(SerializeMany(found, missing));

        root.GetProperty("exitCode").GetInt32().Should()
            .Be(ExitCodes.ValidationError)
            .And.Be(PackageOriginService.CombineExitCodes([ExitCodes.Success, ExitCodes.ValidationError]));
        root.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("exitCode").GetInt32())
            .Should()
            .Equal(ExitCodes.Success, ExitCodes.ValidationError);
    }

    [Fact]
    public async Task SerializeMany_IncompleteScan_OutranksEveryOtherVerdict()
    {
        // A found answer over an unreadable project exits 8 on the terminal path too — absence
        // proven only over half a workspace is not absence, and neither is presence.
        var request = BuildRequest(
            references: [Resolved("App", isTransitive: false)],
            declaredReferences: [Declared("App")],
            projectPaths: [ProjectPath("App"), ProjectPath("Worker")],
            scanOutcomes:
            [
                new(ProjectPath("App"), ResolvedRead: true, DeclarationsRead: true),
                new(ProjectPath("Worker"), ResolvedRead: false, DeclarationsRead: false),
            ],
            failedScanCount: 1
        );

        var consoleExitCode = await ConsoleRunAsync(request);
        var root = Parse(SerializeMany(request));

        root.GetProperty("exitCode").GetInt32().Should()
            .Be(ExitCodes.IncompleteAnalysis)
            .And.Be(consoleExitCode);
        var answer = root.GetProperty("results").EnumerateArray().Single();
        answer.GetProperty("summary").GetProperty("unreadable").GetInt32().Should().Be(1);
        answer.GetProperty("summary").GetProperty("failedScans").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// The whole parity contract in one assertion: the quiet path and the rendering path are asked
    /// about the same workspace, and must answer with the same number.
    /// </summary>
    private static async Task AssertParity(PackageOriginRequest request, int expected)
    {
        var consoleExitCode = await ConsoleRunAsync(request);
        var (_, quietExitCode) = PackageOriginService.AnalyzeQuietly(request);

        consoleExitCode.Should().Be(expected);
        quietExitCode.Should().Be(expected);
        Parse(Serialize(request))
            .GetProperty("exitCode")
            .GetInt32()
            .Should()
            .Be(expected);
    }

    private static async Task<int> ConsoleRunAsync(PackageOriginRequest request)
    {
        return await new PackageOriginService(new FakeConsoleService()).RunAsync(request);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Project(JsonElement root, string relativePath)
    {
        return root
            .GetProperty("projects")
            .EnumerateArray()
            .Single(p => p.GetProperty("relativePath").GetString() == relativePath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Builders, mirroring PackageOriginServiceTests
    // ─────────────────────────────────────────────────────────────────────────

    private static string ProjectPath(string projectName) =>
        $"/ws/src/{projectName}/{projectName}.csproj";

    private static string SerializeMany(params PackageOriginRequest[] requests)
    {
        var answers = requests
            .Select(request =>
            {
                var (report, exitCode) = PackageOriginService.AnalyzeQuietly(request);
                return (request, report, exitCode);
            })
            .ToList();
        var exitCode = PackageOriginService.CombineExitCodes(
            [.. answers.Select(answer => answer.exitCode)]
        );

        return PackageOriginJsonWriter.SerializeMany(answers, exitCode);
    }

    private static PackageReference Resolved(
        string projectName,
        string version = "4.0.2",
        bool isTransitive = false
    ) => new(PackageId, version, ProjectPath(projectName), projectName, isTransitive);

    private static PackageReference OtherResolved(string projectName, string packageName) =>
        new(packageName, "1.0.0", ProjectPath(projectName), projectName);

    private static PackageReference Declared(string projectName) =>
        new(PackageId, "", ProjectPath(projectName), projectName);

    private static ProjectResolvedGraph GraphWithIntroducer(string projectName)
    {
        return new ProjectResolvedGraph(
            ProjectPath(projectName),
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
        IReadOnlyList<ProjectResolvedGraph>? graphs = null,
        IReadOnlyList<string>? projectPaths = null,
        IReadOnlyList<PackageOriginProjectScan>? scanOutcomes = null,
        int failedScanCount = 0
    )
    {
        var paths = projectPaths ?? [ProjectPath("App"), ProjectPath("Worker")];
        return new PackageOriginRequest(
            PackageId,
            new ProjectPackageInfo(
                references,
                BasePath: "/ws",
                DeclaredReferences: declaredReferences
            ),
            graphs ?? [],
            paths.Count,
            failedScanCount,
            paths,
            scanOutcomes
        );
    }

    private static string Serialize(PackageOriginRequest request)
    {
        var (report, exitCode) = PackageOriginService.AnalyzeQuietly(request);
        return PackageOriginJsonWriter.Serialize(request, report, exitCode);
    }
}
