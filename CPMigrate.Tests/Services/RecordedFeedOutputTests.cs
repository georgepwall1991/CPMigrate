using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Drives the four feed-dependent rules with <em>recorded real output</em> from
/// <c>dotnet package list</c>, so nothing is exempt from the guarantee
/// <see cref="EveryRuleCanFireTests"/> makes.
///
/// Those four were exempted there on the grounds that they need a live NuGet feed. That is true of the
/// *query*, but not of everything after it — and everything after it is where the defect in 3.20.0 lived:
/// a parser reading a shape the feed does not actually produce, reporting nothing, and looking clean while
/// doing it. Exempting the rules left exactly that part unexamined.
///
/// Every JSON payload below was captured from a real <c>dotnet package list</c> run against a real
/// restored project, not written from the documentation. That distinction is the whole point: the 3.20.0
/// bug survived because its fixtures were plausible rather than real. Notable things the real output does
/// that a hand-written fixture would not:
/// <list type="bullet">
///   <item>the advisory URL key is <c>advisoryurl</c>, all lower case, unlike every neighbouring key;</item>
///   <item>a project with no findings has <b>no <c>frameworks</c> key at all</b>, just a <c>path</c>;</item>
///   <item>transitive packages carry a <c>resolvedVersion</c> but no <c>requestedVersion</c>.</item>
/// </list>
/// </summary>
public class RecordedFeedOutputTests
{
    private const string ProjectPath = "/repo/src/Api/Api.csproj";

    /// <summary>
    /// Captured from `dotnet package list --vulnerable --include-transitive --format json` against a
    /// project referencing Newtonsoft.Json 9.0.1 and System.Text.RegularExpressions 4.3.0.
    /// </summary>
    private const string VulnerableOutput = """
        {
          "version": 1,
          "parameters": "--vulnerable --include-transitive",
          "sources": [ "https://api.nuget.org/v3/index.json" ],
          "projects": [
            {
              "path": "/repo/src/Api/Api.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    {
                      "id": "Newtonsoft.Json",
                      "requestedVersion": "9.0.1",
                      "resolvedVersion": "9.0.1",
                      "vulnerabilities": [
                        {
                          "severity": "High",
                          "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"
                        }
                      ]
                    },
                    {
                      "id": "System.Text.RegularExpressions",
                      "requestedVersion": "4.3.0",
                      "resolvedVersion": "4.3.0",
                      "vulnerabilities": [
                        {
                          "severity": "High",
                          "advisoryurl": "https://github.com/advisories/GHSA-cmhx-cq75-c4mj"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>Captured from `dotnet package list --outdated --format json`.</summary>
    private const string OutdatedOutput = """
        {
          "version": 1,
          "parameters": "--outdated",
          "sources": [ "https://api.nuget.org/v3/index.json" ],
          "projects": [
            {
              "path": "/repo/src/Api/Api.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    {
                      "id": "Newtonsoft.Json",
                      "requestedVersion": "9.0.1",
                      "resolvedVersion": "9.0.1",
                      "latestVersion": "13.0.4"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Captured from `dotnet package list --deprecated --format json` against a project referencing
    /// Microsoft.AspNetCore.Http.Abstractions 2.2.0.
    /// </summary>
    private const string DeprecatedOutput = """
        {
          "version": 1,
          "parameters": "--deprecated",
          "sources": [ "https://api.nuget.org/v3/index.json" ],
          "projects": [
            {
              "path": "/repo/src/Api/Api.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    {
                      "id": "Microsoft.AspNetCore.Http.Abstractions",
                      "requestedVersion": "2.2.0",
                      "resolvedVersion": "2.2.0",
                      "deprecationReasons": [ "Other", "Legacy" ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Captured from `dotnet package list --include-transitive --format json` against a project
    /// referencing Serilog.Sinks.File 7.0.0, which pulls Serilog transitively.
    /// </summary>
    private const string TransitiveOutput = """
        {
          "version": 1,
          "parameters": "--include-transitive",
          "sources": [ "https://api.nuget.org/v3/index.json" ],
          "projects": [
            {
              "path": "/repo/src/Api/Api.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    { "id": "Serilog.Sinks.File", "requestedVersion": "7.0.0", "resolvedVersion": "7.0.0" }
                  ],
                  "transitivePackages": [
                    { "id": "Serilog", "resolvedVersion": "4.2.0" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// The same query against a second project, where the transitive Serilog settles on a different
    /// version. Two projects disagreeing about a package neither references directly is what
    /// TransitiveConflict is for, and the reason central pinning exists.
    /// </summary>
    private const string TransitiveOutputOtherProject = """
        {
          "version": 1,
          "parameters": "--include-transitive",
          "sources": [ "https://api.nuget.org/v3/index.json" ],
          "projects": [
            {
              "path": "/repo/src/Worker/Worker.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    { "id": "Serilog.Sinks.Console", "requestedVersion": "6.0.0", "resolvedVersion": "6.0.0" }
                  ],
                  "transitivePackages": [
                    { "id": "Serilog", "resolvedVersion": "4.1.0" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// What a project with nothing to report looks like: no <c>frameworks</c> key at all. Worth its own
    /// constant because a hand-written fixture would have included an empty array, and code that assumes
    /// one throws here.
    /// </summary>
    private const string NothingToReportOutput = """
        {
          "version": 1,
          "parameters": "--deprecated",
          "sources": [ "https://api.nuget.org/v3/index.json" ],
          "projects": [ { "path": "/repo/src/Api/Api.csproj" } ]
        }
        """;

    [Fact]
    public async Task SecurityVulnerability_FiresFromRecordedOutput()
    {
        var (vulnerabilities, success) = await Query(VulnerableOutput)
            .ScanVulnerabilitiesAsync(ProjectPath);

        success.Should().BeTrue();
        var report = Analyze(new ProjectPackageInfo([], vulnerabilities, BasePath: "/repo"));

        Codes(report).Should().Contain(nameof(AnalysisIssueCode.SecurityVulnerability));

        // The advisory link is the only actionable part of a vulnerability finding, and its key is the one
        // that does not match the casing of its neighbours.
        // The parser stores the advisory URL as the vulnerability's Id — it is the only identifier the
        // feed gives, and the only actionable part of the finding.
        vulnerabilities
            .Should()
            .OnlyContain(v => v.Id.StartsWith("https://github.com/advisories/", StringComparison.Ordinal));
        vulnerabilities.Should().OnlyContain(v => v.Severity == "High");
    }

    [Fact]
    public async Task OutdatedPackage_FiresFromRecordedOutput()
    {
        var (outdated, success) = await Query(OutdatedOutput)
            .ScanOutdatedPackagesAsync(ProjectPath, includeTransitive: false);

        success.Should().BeTrue();
        var report = Analyze(
            new ProjectPackageInfo([], OutdatedPackages: outdated, BasePath: "/repo")
        );

        Codes(report).Should().Contain(nameof(AnalysisIssueCode.OutdatedPackage));
        outdated.Should().ContainSingle(p => p.LatestVersion == "13.0.4");
    }

    [Fact]
    public async Task DeprecatedPackage_FiresFromRecordedOutput()
    {
        var (deprecated, success) = await Query(DeprecatedOutput)
            .ScanDeprecatedPackagesAsync(ProjectPath, includeTransitive: false);

        success.Should().BeTrue();
        var report = Analyze(
            new ProjectPackageInfo([], DeprecatedPackages: deprecated, BasePath: "/repo")
        );

        Codes(report).Should().Contain(nameof(AnalysisIssueCode.DeprecatedPackage));
        deprecated.Should().ContainSingle(p => p.Reasons.Contains("Legacy"));
    }

    [Fact]
    public async Task TransitiveConflict_FiresFromRecordedOutput()
    {
        // What the rule actually detects: two projects landing on different versions of a package neither
        // references directly, which is the case central pinning exists to settle. My first attempt asserted
        // a direct-versus-transitive difference — which no rule claims to report, and NuGet resolves on its
        // own. Worth naming, because a test asserting the wrong contract is how a rule ends up looking
        // covered when it is not.
        var (api, apiOk) = await Query(TransitiveOutput)
            .ScanResolvedPackagesAsync(ProjectPath, includeTransitive: true);
        var (worker, workerOk) = await Query(TransitiveOutputOtherProject)
            .ScanResolvedPackagesAsync("/repo/src/Worker/Worker.csproj", includeTransitive: true);

        apiOk.Should().BeTrue();
        workerOk.Should().BeTrue();
        api.Should()
            .Contain(
                r => r.IsTransitive && r.PackageName == "Serilog",
                "the transitive entry must be marked as one, or no rule can tell it apart"
            );

        var report = Analyze(new ProjectPackageInfo([.. api, .. worker], BasePath: "/repo"));

        Codes(report).Should().Contain(nameof(AnalysisIssueCode.TransitiveConflict));
    }

    [Fact]
    public async Task ATransitiveEntry_IsNotCountedAsADuplicateDeclaration()
    {
        // Found by the test above: with the declared list unavailable the resolved one stands in, and under
        // --transitive it holds the same package twice — once directly, once as a transitive of something
        // else. That read as the project declaring it twice, inventing a RedundantReference finding for a
        // project with one perfectly ordinary reference.
        var (references, _) = await Query(
                """
                {
                  "version": 1,
                  "parameters": "--include-transitive",
                  "projects": [
                    {
                      "path": "/repo/src/Api/Api.csproj",
                      "frameworks": [
                        {
                          "framework": "net10.0",
                          "topLevelPackages": [
                            { "id": "Serilog", "requestedVersion": "4.3.0", "resolvedVersion": "4.3.0" }
                          ],
                          "transitivePackages": [
                            { "id": "Serilog", "resolvedVersion": "4.3.0" }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """
            )
            .ScanResolvedPackagesAsync(ProjectPath, includeTransitive: true);

        var report = Analyze(new ProjectPackageInfo(references, BasePath: "/repo"));

        Codes(report).Should().NotContain(nameof(AnalysisIssueCode.RedundantReference));
    }

    [Fact]
    public async Task AProjectWithNoFindings_HasNoFrameworksKeyAndIsHandled()
    {
        // The shape a hand-written fixture would get wrong: no `frameworks` key, not an empty array.
        var service = Query(NothingToReportOutput);

        var (deprecated, deprecatedOk) = await service.ScanDeprecatedPackagesAsync(ProjectPath, includeTransitive: false);
        var (vulnerabilities, vulnerableOk) = await service.ScanVulnerabilitiesAsync(ProjectPath);
        var (outdated, outdatedOk) = await service.ScanOutdatedPackagesAsync(ProjectPath, includeTransitive: false);

        deprecatedOk.Should().BeTrue("a project with nothing to report is a successful query");
        vulnerableOk.Should().BeTrue();
        outdatedOk.Should().BeTrue();
        deprecated.Should().BeEmpty();
        vulnerabilities.Should().BeEmpty();
        outdated.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedQuery_IsReportedAsFailureRatherThanAsNoFindings()
    {
        // The distinction this release series keeps coming back to. A query that did not run must not be
        // indistinguishable from one that found nothing, or a scan of a vulnerable solution reads clean.
        var cli = new Mock<IDotNetCliService>();
        cli.Setup(c =>
                c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>())
            )
            .ReturnsAsync((string.Empty, false));
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        var (vulnerabilities, success) = await service.ScanVulnerabilitiesAsync(ProjectPath);

        success.Should().BeFalse();
        vulnerabilities.Should().BeEmpty();
    }

    private static DotNetPackageQueryService Query(string recordedJson)
    {
        var cli = new Mock<IDotNetCliService>();
        cli.Setup(c =>
                c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>())
            )
            .ReturnsAsync((recordedJson, true));

        return new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);
    }

    private static AnalysisReport Analyze(ProjectPackageInfo packageInfo)
    {
        return new AnalysisService().Analyze(packageInfo);
    }

    private static List<string> Codes(AnalysisReport report)
    {
        return report
            .Results.SelectMany(result => result.Issues)
            .Select(issue => issue.IssueCode.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
