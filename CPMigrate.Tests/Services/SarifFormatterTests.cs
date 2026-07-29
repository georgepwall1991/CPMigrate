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
