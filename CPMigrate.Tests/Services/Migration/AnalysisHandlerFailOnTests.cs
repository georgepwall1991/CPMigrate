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
    /// Builds a handler whose scan yields exactly one finding at <paramref name="severity"/>, via a
    /// version inconsistency between two projects.
    /// </summary>
    private AnalysisHandler CreateHandler(
        AnalysisSeverity severity,
        bool referenceScanSucceeds = true
    )
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
            new StubAnalysisService(severity),
            new FixService(SilentConsoleService.Instance),
            SilentConsoleService.Instance,
            quietMode: true,
            _ => Task.FromResult((_root, new List<string> { _projectPath }))
        );
    }

    /// <summary>
    /// Returns a single finding at a chosen severity, so each test controls exactly one variable.
    /// </summary>
    private sealed class StubAnalysisService(AnalysisSeverity severity) : IAnalysisService
    {
        public AnalysisReport Analyze(ProjectPackageInfo packageInfo)
        {
            return new AnalysisReport(
                packageInfo.ProjectCount,
                packageInfo.TotalReferences,
                new[]
                {
                    new AnalyzerResult(
                        "Stub",
                        new[]
                        {
                            new AnalysisIssue(
                                "Newtonsoft.Json",
                                $"A {severity} finding.",
                                new[] { "Api.csproj" },
                                AnalysisIssueCode.VersionInconsistency,
                                severity
                            ),
                        }
                    ),
                }
            );
        }
    }
}
