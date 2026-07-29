using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

/// <summary>
/// The <c>--fail-on</c> threshold decides when findings become a build failure. Without it a team
/// with existing informational debt cannot adopt the gate at all: every run fails, so the signal
/// gets ignored and a real vulnerability lands with it.
/// </summary>
public class AnalysisHandlerFailOnTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectPath;

    public AnalysisHandlerFailOnTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateFailOn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _projectPath = Path.Combine(_root, "Api.csproj");
        File.WriteAllText(_projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
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
    public async Task ExecuteAsync_DefaultThreshold_FailsOnAnyFinding()
    {
        // The pre-3.8.0 behaviour: any issue is a failure. Adding the flag must not change what a
        // pipeline that does not pass it sees.
        var handler = CreateHandler(AnalysisSeverity.Info);

        var result = await handler.ExecuteAsync(
            new Options
            {
                Analyze = true,
                Quiet = true,
                SolutionFileDir = _root,
            }
        );

        result.ExitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task ExecuteAsync_FindingBelowThreshold_ExitsSuccess()
    {
        var handler = CreateHandler(AnalysisSeverity.Low);

        var result = await handler.ExecuteAsync(FailOn(FailOnSeverity.High));

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task ExecuteAsync_FindingAtThreshold_Fails()
    {
        var handler = CreateHandler(AnalysisSeverity.High);

        var result = await handler.ExecuteAsync(FailOn(FailOnSeverity.High));

        result.ExitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task ExecuteAsync_FindingAboveThreshold_Fails()
    {
        var handler = CreateHandler(AnalysisSeverity.Critical);

        var result = await handler.ExecuteAsync(FailOn(FailOnSeverity.High));

        result.ExitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task ExecuteAsync_FailOnNever_ExitsSuccessEvenForCriticalFindings()
    {
        // Report-only mode: the findings still reach SARIF/JSON, but the build is not gated on them.
        var handler = CreateHandler(AnalysisSeverity.Critical);

        var result = await handler.ExecuteAsync(FailOn(FailOnSeverity.Never));

        result.ExitCode.Should().Be(ExitCodes.Success);
        result
            .AnalysisReport!.HasIssues.Should()
            .BeTrue("suppressing the gate must not suppress the report");
    }

    [Fact]
    public async Task ExecuteAsync_BelowThresholdButScanIncomplete_StillReportsIncomplete()
    {
        // --fail-on is about which findings matter, not about whether the scan can be trusted.
        // An unexamined project is not something a severity threshold should be able to hide.
        var handler = CreateHandler(AnalysisSeverity.Low, referenceScanSucceeds: false);

        var result = await handler.ExecuteAsync(FailOn(FailOnSeverity.Never));

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
    }

    [Fact]
    public async Task ExecuteAsync_FixRepairsSomeButAnUnfixableFindingRemains_StillGates()
    {
        // The realistic shape of this: --fix repairs a version inconsistency while a Critical CVE
        // sits alongside it, unfixable by definition. Treating the whole run as clean because
        // *something* was fixed reports a live vulnerability as success.
        var handler = CreateHandler(
            new AnalysisIssue(
                "Newtonsoft.Json",
                "Version inconsistency.",
                new[] { "Api.csproj" },
                AnalysisIssueCode.VersionInconsistency,
                AnalysisSeverity.Moderate,
                Fixable: true
            ),
            new AnalysisIssue(
                "System.Text.Json",
                "Critical severity vulnerability.",
                new[] { "Api.csproj" },
                AnalysisIssueCode.SecurityVulnerability,
                AnalysisSeverity.Critical,
                Fixable: false
            )
        );

        var options = FailOn(FailOnSeverity.High);
        options.Fix = true;

        var result = await handler.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
        result.GatedIssueCount.Should().Be(1, "the unfixable Critical finding is still on disk");
    }

    [Fact]
    public async Task ExecuteAsync_FixRepairsEverythingAtOrAboveTheThreshold_ExitsSuccess()
    {
        // The mirror case: everything that would have gated was repaired, so the run is clean.
        var handler = CreateHandler(
            new AnalysisIssue(
                "Newtonsoft.Json",
                "Version inconsistency.",
                new[] { "Api.csproj" },
                AnalysisIssueCode.VersionInconsistency,
                AnalysisSeverity.Moderate,
                Fixable: true
            ),
            new AnalysisIssue(
                "Legacy.Package",
                "Deprecated package.",
                new[] { "Api.csproj" },
                AnalysisIssueCode.DeprecatedPackage,
                AnalysisSeverity.Low,
                Fixable: false
            )
        );

        var options = FailOn(FailOnSeverity.High);
        options.Fix = true;

        var result = await handler.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.GatedIssueCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_FixWithUnfixableFindingBelowTheThreshold_ExitsSuccess()
    {
        var handler = CreateHandler(
            new AnalysisIssue(
                "Legacy.Package",
                "Deprecated package.",
                new[] { "Api.csproj" },
                AnalysisIssueCode.DeprecatedPackage,
                AnalysisSeverity.Moderate,
                Fixable: false
            )
        );

        var options = FailOn(FailOnSeverity.Critical);
        options.Fix = true;

        var result = await handler.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    private Options FailOn(FailOnSeverity threshold)
    {
        return new Options
        {
            Analyze = true,
            Quiet = true,
            SolutionFileDir = _root,
            FailOn = threshold,
        };
    }

    /// <summary>
    /// Builds a handler whose analysis yields exactly one finding at the given severity.
    /// </summary>
    private AnalysisHandler CreateHandler(params AnalysisIssue[] issues)
    {
        return CreateHandlerCore(true, issues);
    }

    private AnalysisHandler CreateHandler(
        AnalysisSeverity severity,
        bool referenceScanSucceeds = true
    )
    {
        return CreateHandlerCore(
            referenceScanSucceeds,
            [
                new AnalysisIssue(
                    "Newtonsoft.Json",
                    $"A {severity} finding.",
                    new[] { "Api.csproj" },
                    AnalysisIssueCode.VersionInconsistency,
                    severity
                ),
            ]
        );
    }

    private AnalysisHandler CreateHandlerCore(bool referenceScanSucceeds, AnalysisIssue[] issues)
    {
        var projectAnalyzer = new Mock<IProjectAnalyzer>();
        projectAnalyzer
            .Setup(a => a.ScanResolvedPackagesAsync(_projectPath, It.IsAny<bool>()))
            .ReturnsAsync(
                (
                    new List<PackageReference>
                    {
                        new("Newtonsoft.Json", "13.0.1", _projectPath, "Api.csproj"),
                    },
                    referenceScanSucceeds
                )
            );
        projectAnalyzer
            .Setup(a => a.ScanProjectPackages(_projectPath))
            .Returns((new List<PackageReference>(), referenceScanSucceeds));

        return new AnalysisHandler(
            projectAnalyzer.Object,
            new StubAnalysisService(issues),
            new FixService(SilentConsoleService.Instance),
            SilentConsoleService.Instance,
            quietMode: true,
            _ => Task.FromResult((_root, new List<string> { _projectPath }))
        );
    }

    /// <summary>
    /// Returns the findings the test supplied, so each test controls exactly one variable.
    /// </summary>
    private sealed class StubAnalysisService(AnalysisIssue[] issues) : IAnalysisService
    {
        public AnalysisReport Analyze(ProjectPackageInfo packageInfo)
        {
            return new AnalysisReport(
                packageInfo.ProjectCount,
                packageInfo.TotalReferences,
                new[] { new AnalyzerResult("Stub", issues) }
            );
        }
    }
}
