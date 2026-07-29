using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Contract tests for the SARIF 2.1.0 emitter. GitHub code scanning rejects logs that
/// deviate from the schema, so these assert the exact shape consumers depend on:
/// tool driver metadata, a rule per issue code, severity→level mapping, artifact
/// locations resolved relative to the scan root, and stable partial fingerprints.
/// </summary>
public class SarifFormatterTests : IDisposable
{
    private readonly string _root;

    public SarifFormatterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateSarif_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Format_EmptyReport_ProducesValidSarifSkeleton()
    {
        var report = new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());

        var doc = FormatToDocument(report, new ProjectPackageInfo(Array.Empty<PackageReference>()));

        doc.RootElement.GetProperty("version").GetString().Should().Be("2.1.0");
        doc.RootElement.GetProperty("$schema")
            .GetString()
            .Should()
            .Be(
                "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json"
            );

        var runs = doc.RootElement.GetProperty("runs");
        runs.GetArrayLength().Should().Be(1);

        var driver = runs[0].GetProperty("tool").GetProperty("driver");
        driver.GetProperty("name").GetString().Should().Be("CPMigrate");
        driver.GetProperty("version").GetString().Should().Be(OutputMetadata.CurrentVersion);
        driver.GetProperty("informationUri").GetString().Should().Contain("github.com");

        runs[0].GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Format_EmitsOneRulePerDistinctIssueCode()
    {
        var report = new AnalysisReport(
            2,
            4,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        Issue(
                            "Newtonsoft.Json",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate
                        ),
                        Issue(
                            "Serilog",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate
                        ),
                    }
                ),
                new AnalyzerResult(
                    "Security Vulnerabilities",
                    new[]
                    {
                        Issue(
                            "System.Text.Json",
                            AnalysisIssueCode.SecurityVulnerability,
                            AnalysisSeverity.Critical
                        ),
                    }
                ),
            }
        );

        var doc = FormatToDocument(report, new ProjectPackageInfo(Array.Empty<PackageReference>()));
        var rules = doc
            .RootElement.GetProperty("runs")[0]
            .GetProperty("tool")
            .GetProperty("driver")
            .GetProperty("rules");

        var ruleIds = rules.EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        ruleIds.Should().BeEquivalentTo(new[] { "VersionInconsistency", "SecurityVulnerability" });

        var versionRule = rules
            .EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == "VersionInconsistency");
        versionRule
            .GetProperty("shortDescription")
            .GetProperty("text")
            .GetString()
            .Should()
            .NotBeNullOrWhiteSpace();
        versionRule
            .GetProperty("fullDescription")
            .GetProperty("text")
            .GetString()
            .Should()
            .NotBeNullOrWhiteSpace();
        versionRule.GetProperty("helpUri").GetString().Should().StartWith("https://");
        versionRule
            .GetProperty("properties")
            .GetProperty("tags")
            .EnumerateArray()
            .Select(t => t.GetString())
            .Should()
            .Contain("dependencies");
    }

    [Fact]
    public void Format_ResultsPointAtTheirRuleByIndex()
    {
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Security Vulnerabilities",
                    new[]
                    {
                        Issue(
                            "System.Text.Json",
                            AnalysisIssueCode.SecurityVulnerability,
                            AnalysisSeverity.Critical
                        ),
                    }
                ),
            }
        );

        var run = FormatToDocument(report, new ProjectPackageInfo(Array.Empty<PackageReference>()))
            .RootElement.GetProperty("runs")[0];

        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules");
        var result = run.GetProperty("results")[0];

        var index = result.GetProperty("ruleIndex").GetInt32();
        rules[index]
            .GetProperty("id")
            .GetString()
            .Should()
            .Be(result.GetProperty("ruleId").GetString());
    }

    [Theory]
    [InlineData(AnalysisSeverity.Critical, "error")]
    [InlineData(AnalysisSeverity.High, "error")]
    [InlineData(AnalysisSeverity.Moderate, "warning")]
    [InlineData(AnalysisSeverity.Low, "note")]
    [InlineData(AnalysisSeverity.Info, "note")]
    public void Format_MapsSeverityToSarifLevel(AnalysisSeverity severity, string expectedLevel)
    {
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Analyzer",
                    new[] { Issue("Pkg", AnalysisIssueCode.OutdatedPackage, severity) }
                ),
            }
        );

        var result = FormatToDocument(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>())
        )
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0];

        result.GetProperty("level").GetString().Should().Be(expectedLevel);
        result
            .GetProperty("properties")
            .GetProperty("severity")
            .GetString()
            .Should()
            .Be(severity.ToString());
    }

    [Fact]
    public void Format_ResolvesAffectedProjectsToRepositoryRelativeUris()
    {
        var projectPath = CreateProject("src/Api/Api.csproj", "Newtonsoft.Json", "13.0.1");
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Newtonsoft.Json", "13.0.1", projectPath, "Api.csproj") }
        );

        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        Issue(
                            "Newtonsoft.Json",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate,
                            "Api.csproj"
                        ),
                    }
                ),
            }
        );

        var location = FormatToDocument(report, packageInfo)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation");

        location
            .GetProperty("artifactLocation")
            .GetProperty("uri")
            .GetString()
            .Should()
            .Be("src/Api/Api.csproj");
        location
            .GetProperty("artifactLocation")
            .GetProperty("uriBaseId")
            .GetString()
            .Should()
            .Be("SRCROOT");
    }

    [Fact]
    public void Format_IncompleteScan_ReportsAnUnsuccessfulInvocation()
    {
        var report = new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());

        var json = SarifFormatter.Format(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>()),
            _root,
            new SarifRunOutcome(false, "No projects were found to analyze.")
        );

        var invocation = JsonDocument
            .Parse(json)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0];

        invocation
            .GetProperty("executionSuccessful")
            .GetBoolean()
            .Should()
            .BeFalse(
                "an empty result set from an incomplete scan is a false negative, not a clean bill of health"
            );
        invocation
            .GetProperty("toolExecutionNotifications")[0]
            .GetProperty("message")
            .GetProperty("text")
            .GetString()
            .Should()
            .Be("No projects were found to analyze.");
    }

    [Fact]
    public void Format_CompleteScan_ReportsASuccessfulInvocation()
    {
        var report = new AnalysisReport(1, 1, Array.Empty<AnalyzerResult>());

        var json = SarifFormatter.Format(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>()),
            _root,
            SarifRunOutcome.Successful
        );

        var invocation = JsonDocument
            .Parse(json)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0];

        invocation.GetProperty("executionSuccessful").GetBoolean().Should().BeTrue();
        invocation.TryGetProperty("toolExecutionNotifications", out _).Should().BeFalse();
    }

    [Fact]
    public void FormatError_ReportsAnUnsuccessfulInvocationRatherThanAFinding()
    {
        var doc = JsonDocument.Parse(SarifFormatter.FormatError("Solution file not found.", _root));

        var run = doc.RootElement.GetProperty("runs")[0];
        run.GetProperty("results")
            .GetArrayLength()
            .Should()
            .Be(0, "a tool failure is not a code finding");

        var invocation = run.GetProperty("invocations")[0];
        invocation.GetProperty("executionSuccessful").GetBoolean().Should().BeFalse();

        var notification = invocation.GetProperty("toolExecutionNotifications")[0];
        notification.GetProperty("level").GetString().Should().Be("error");
        notification
            .GetProperty("message")
            .GetProperty("text")
            .GetString()
            .Should()
            .Be("Solution file not found.");
    }

    [Fact]
    public void Format_PointsAtTheLineDeclaringTheOffendingPackage()
    {
        var projectPath = CreateProject("src/Api/Api.csproj", "Newtonsoft.Json", "13.0.1");
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Newtonsoft.Json", "13.0.1", projectPath, "Api.csproj") }
        );

        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        Issue(
                            "Newtonsoft.Json",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate,
                            "Api.csproj"
                        ),
                    }
                ),
            }
        );

        var region = FormatToDocument(report, packageInfo)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("region");

        // The fixture writes the PackageReference on line 4.
        region.GetProperty("startLine").GetInt32().Should().Be(4);
    }

    [Fact]
    public void Format_ProjectAboveTheScanRoot_StaysRepositoryRelative()
    {
        // A solution under build/ referencing ../src/App.csproj is a common layout. Emitting a
        // runner-absolute file:// URI for it would leave code scanning unable to map the finding
        // back to a checked-out file, losing the annotation entirely.
        var projectPath = CreateProject("src/App/App.csproj", "Newtonsoft.Json", "13.0.1");
        var solutionDirectory = Path.Combine(_root, "build");
        Directory.CreateDirectory(solutionDirectory);

        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Newtonsoft.Json", "13.0.1", projectPath, "App.csproj") }
        );
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        Issue(
                            "Newtonsoft.Json",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate,
                            "App.csproj"
                        ),
                    }
                ),
            }
        );

        var json = SarifFormatter.Format(report, packageInfo, solutionDirectory);
        var run = JsonDocument.Parse(json).RootElement.GetProperty("runs")[0];

        var uri = run.GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation")
            .GetProperty("uri")
            .GetString();

        uri.Should().Be("src/App/App.csproj", "the URI base widens to cover every reported file");
        uri.Should().NotStartWith("file://").And.NotStartWith("..");

        run.GetProperty("originalUriBaseIds")
            .GetProperty("SRCROOT")
            .GetProperty("uri")
            .GetString()
            .Should()
            .Be(new Uri(_root + Path.DirectorySeparatorChar).AbsoluteUri);
    }

    [Fact]
    public void Format_SkipsCommentedOutReferencesWhenLocatingTheLine()
    {
        var projectPath = Path.Combine(_root, "Commented.csproj");
        File.WriteAllText(
            projectPath,
            string.Join(
                Environment.NewLine,
                new[]
                {
                    "<Project Sdk=\"Microsoft.NET.Sdk\">",
                    "  <ItemGroup>",
                    "    <!-- <PackageReference Include=\"Newtonsoft.Json\" Version=\"9.0.0\" /> -->",
                    "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />",
                    "  </ItemGroup>",
                    "</Project>",
                }
            )
        );

        var packageInfo = new ProjectPackageInfo(
            new[]
            {
                new PackageReference("Newtonsoft.Json", "13.0.1", projectPath, "Commented.csproj"),
            }
        );
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        Issue(
                            "Newtonsoft.Json",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate,
                            "Commented.csproj"
                        ),
                    }
                ),
            }
        );

        var region = FormatToDocument(report, packageInfo)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("region");

        region
            .GetProperty("startLine")
            .GetInt32()
            .Should()
            .Be(4, "the annotation must land on the live reference, not the commented-out one");
    }

    [Fact]
    public void Format_MultiLineCommentHidingAReference_IsStillSkipped()
    {
        var projectPath = Path.Combine(_root, "MultiLine.csproj");
        File.WriteAllText(
            projectPath,
            string.Join(
                Environment.NewLine,
                new[]
                {
                    "<Project Sdk=\"Microsoft.NET.Sdk\">",
                    "  <!--",
                    "    <PackageReference Include=\"Serilog\" Version=\"1.0.0\" />",
                    "  -->",
                    "  <ItemGroup>",
                    "    <PackageReference Include=\"Serilog\" Version=\"4.3.0\" />",
                    "  </ItemGroup>",
                    "</Project>",
                }
            )
        );

        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "4.3.0", projectPath, "MultiLine.csproj") }
        );
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        Issue(
                            "Serilog",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate,
                            "MultiLine.csproj"
                        ),
                    }
                ),
            }
        );

        var region = FormatToDocument(report, packageInfo)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("region");

        region.GetProperty("startLine").GetInt32().Should().Be(6);
    }

    [Fact]
    public void Format_PercentEncodesReservedCharactersInArtifactUris()
    {
        // artifactLocation.uri is a URI reference, not a filesystem path. A raw space or '#'
        // makes it invalid, and a consumer either rejects it or resolves it to the wrong file.
        var projectPath = CreateProject("src/My App #2/App.csproj", "Newtonsoft.Json", "13.0.1");
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Newtonsoft.Json", "13.0.1", projectPath, "App.csproj") }
        );

        var uri = FormatToDocument(packageInfo, "App.csproj")
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation")
            .GetProperty("uri")
            .GetString();

        uri.Should().Be("src/My%20App%20%232/App.csproj");
        Uri.IsWellFormedUriString(uri, UriKind.Relative).Should().BeTrue();
    }

    [Theory]
    [InlineData("    <PackageReference Include='Serilog' Version='4.3.0' />", 4)]
    [InlineData("    <PackageReference Update=\"Serilog\" Version=\"4.3.0\" />", 4)]
    [InlineData("    <PackageReference Include=\"Serilog\">", 4)]
    public void Format_LocatesTheDeclarationRegardlessOfAttributeStyle(
        string declaration,
        int expectedLine
    )
    {
        // The declaration is XML, not text: single quotes, an Update= attribute, and a child-element
        // form are all valid and all appear in real projects.
        var projectPath = Path.Combine(_root, "Styles.csproj");
        File.WriteAllText(
            projectPath,
            string.Join(
                Environment.NewLine,
                new[]
                {
                    "<Project Sdk=\"Microsoft.NET.Sdk\">",
                    "  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>",
                    "  <ItemGroup>",
                    declaration,
                    declaration.TrimEnd().EndsWith("/>", StringComparison.Ordinal)
                        ? "    <!-- closed -->"
                        : "    </PackageReference>",
                    "  </ItemGroup>",
                    "</Project>",
                }
            )
        );

        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "4.3.0", projectPath, "Styles.csproj") }
        );

        var region = FormatToDocument(packageInfo, "Styles.csproj", "Serilog")
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("region");

        region.GetProperty("startLine").GetInt32().Should().Be(expectedLine);
    }

    [Fact]
    public void Format_MalformedProjectFile_FallsBackToAFileLevelLocation()
    {
        var projectPath = Path.Combine(_root, "Broken.csproj");
        File.WriteAllText(projectPath, "<Project><ItemGroup><PackageReference Include=\"Serilog\"");

        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "4.3.0", projectPath, "Broken.csproj") }
        );

        var physicalLocation = FormatToDocument(packageInfo, "Broken.csproj", "Serilog")
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation");

        physicalLocation
            .GetProperty("artifactLocation")
            .GetProperty("uri")
            .GetString()
            .Should()
            .Be("Broken.csproj");
        physicalLocation
            .TryGetProperty("region", out _)
            .Should()
            .BeFalse("an unparseable project still deserves a file-level annotation");
    }

    /// <summary>
    /// Builds a single-finding report against <paramref name="projectName"/> and formats it.
    /// </summary>
    private JsonDocument FormatToDocument(
        ProjectPackageInfo packageInfo,
        string projectName,
        string packageName = "Newtonsoft.Json"
    )
    {
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        Issue(
                            packageName,
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate,
                            projectName
                        ),
                    }
                ),
            }
        );

        return FormatToDocument(report, packageInfo);
    }

    [Fact]
    public void Format_ProjectsSharingAFileName_AnnotateOnlyTheOnesDeclaringThePackage()
    {
        // src/App/App.csproj and tests/App/App.csproj share a basename, and analyzer findings carry
        // names rather than paths. Annotating both would put a finding on a file that never
        // declared the package.
        var sourceProject = CreateProject("src/App/App.csproj", "Newtonsoft.Json", "13.0.1");
        CreateProject("tests/App/App.csproj", "xunit", "2.9.0");

        var packageInfo = new ProjectPackageInfo(
            new[]
            {
                new PackageReference("Newtonsoft.Json", "13.0.1", sourceProject, "App.csproj"),
                new PackageReference(
                    "xunit",
                    "2.9.0",
                    Path.Combine(_root, "tests", "App", "App.csproj"),
                    "App.csproj"
                ),
            }
        );

        var uris = FormatToDocument(packageInfo, "App.csproj")
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")
            .EnumerateArray()
            .Select(l =>
                l.GetProperty("physicalLocation")
                    .GetProperty("artifactLocation")
                    .GetProperty("uri")
                    .GetString()
            )
            .ToList();

        uris.Should().Equal("src/App/App.csproj");
    }

    [Fact]
    public void Format_AmbiguousNameWithNoPackageMatch_FallsBackToEveryCandidate()
    {
        // A finding that is not about one package (framework alignment, for example) cannot be
        // narrowed by package, so reporting every candidate beats reporting none.
        var first = CreateProject("src/App/App.csproj", "Newtonsoft.Json", "13.0.1");
        var second = CreateProject("tests/App/App.csproj", "Newtonsoft.Json", "13.0.1");

        var packageInfo = new ProjectPackageInfo(
            new[]
            {
                new PackageReference("Newtonsoft.Json", "13.0.1", first, "App.csproj"),
                new PackageReference("Newtonsoft.Json", "13.0.1", second, "App.csproj"),
            }
        );

        var report = new AnalysisReport(
            2,
            2,
            new[]
            {
                new AnalyzerResult(
                    "Framework Alignment",
                    new[]
                    {
                        Issue(
                            "net8.0",
                            AnalysisIssueCode.FrameworkAlignment,
                            AnalysisSeverity.Info,
                            "App.csproj"
                        ),
                    }
                ),
            }
        );

        var uris = FormatToDocument(report, packageInfo)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")
            .EnumerateArray()
            .Select(l =>
                l.GetProperty("physicalLocation")
                    .GetProperty("artifactLocation")
                    .GetProperty("uri")
                    .GetString()
            )
            .ToList();

        uris.Should().BeEquivalentTo(new[] { "src/App/App.csproj", "tests/App/App.csproj" });
    }

    [Fact]
    public void Format_OmitsLocationsWhenNoProjectCanBeResolved()
    {
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Analyzer",
                    new[]
                    {
                        Issue(
                            "Ghost",
                            AnalysisIssueCode.OutdatedPackage,
                            AnalysisSeverity.Low,
                            "Nowhere.csproj"
                        ),
                    }
                ),
            }
        );

        var result = FormatToDocument(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>())
        )
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0];

        result.TryGetProperty("locations", out var locations).Should().BeTrue();
        locations.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Format_PartialFingerprintIsStableAcrossRunsButUniquePerIssue()
    {
        var report = new AnalysisReport(
            1,
            2,
            new[]
            {
                new AnalyzerResult(
                    "Analyzer",
                    new[]
                    {
                        Issue(
                            "A",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate
                        ),
                        Issue(
                            "B",
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate
                        ),
                    }
                ),
            }
        );

        var first = Fingerprints(
            FormatToDocument(report, new ProjectPackageInfo(Array.Empty<PackageReference>()))
        );
        var second = Fingerprints(
            FormatToDocument(report, new ProjectPackageInfo(Array.Empty<PackageReference>()))
        );

        first.Should().Equal(second);
        first.Should().OnlyHaveUniqueItems();
        first.Should().AllSatisfy(f => f.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void Format_CarriesPackageAndFixabilityAsResultProperties()
    {
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "Version Inconsistencies",
                    new[]
                    {
                        new AnalysisIssue(
                            "Newtonsoft.Json",
                            "13.0.1 (Api.csproj), 12.0.3 (Web.csproj)",
                            new[] { "Api.csproj", "Web.csproj" },
                            AnalysisIssueCode.VersionInconsistency,
                            AnalysisSeverity.Moderate,
                            Fixable: true
                        ),
                    }
                ),
            }
        );

        var result = FormatToDocument(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>())
        )
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")[0];

        var properties = result.GetProperty("properties");
        properties.GetProperty("package").GetString().Should().Be("Newtonsoft.Json");
        properties.GetProperty("fixable").GetBoolean().Should().BeTrue();
        properties.GetProperty("analyzer").GetString().Should().Be("Version Inconsistencies");

        result
            .GetProperty("message")
            .GetProperty("text")
            .GetString()
            .Should()
            .Contain("Newtonsoft.Json")
            .And.Contain("12.0.3");
    }

    [Fact]
    public void Format_RecordsScanRootAsOriginalUriBase()
    {
        var report = new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());

        var run = FormatToDocument(report, new ProjectPackageInfo(Array.Empty<PackageReference>()))
            .RootElement.GetProperty("runs")[0];

        var uri = run.GetProperty("originalUriBaseIds")
            .GetProperty("SRCROOT")
            .GetProperty("uri")
            .GetString();
        uri.Should().StartWith("file://").And.EndWith("/");
    }

    private static IReadOnlyList<string> Fingerprints(JsonDocument doc)
    {
        return doc
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")
            .EnumerateArray()
            .Select(r =>
                r.GetProperty("partialFingerprints").GetProperty("cpmigrate/v1").GetString()!
            )
            .ToList();
    }

    private JsonDocument FormatToDocument(AnalysisReport report, ProjectPackageInfo packageInfo)
    {
        var json = SarifFormatter.Format(report, packageInfo, _root);
        return JsonDocument.Parse(json);
    }

    private string CreateProject(string relativePath, string package, string version)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            string.Join(
                Environment.NewLine,
                new[]
                {
                    "<Project Sdk=\"Microsoft.NET.Sdk\">",
                    "  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>",
                    "  <ItemGroup>",
                    $"    <PackageReference Include=\"{package}\" Version=\"{version}\" />",
                    "  </ItemGroup>",
                    "</Project>",
                }
            )
        );
        return fullPath;
    }

    private static AnalysisIssue Issue(
        string package,
        AnalysisIssueCode code,
        AnalysisSeverity severity,
        params string[] affectedProjects
    )
    {
        return new AnalysisIssue(
            package,
            $"{package} has a {code} issue.",
            affectedProjects.Length == 0 ? new[] { "Sample.csproj" } : affectedProjects,
            code,
            severity
        );
    }
}
