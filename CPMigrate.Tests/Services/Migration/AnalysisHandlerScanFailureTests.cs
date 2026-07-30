using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

/// <summary>
/// A finding list is only trustworthy if the scan that produced it completed. These tests pin the
/// distinction: an analyzer query that fails must surface as an incomplete scan rather than as an
/// absence of findings, because a consumer gating on "zero issues" cannot tell the two apart.
/// </summary>
public class AnalysisHandlerScanFailureTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectPath;

    public AnalysisHandlerScanFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateScanFail_{Guid.NewGuid():N}");
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
    public async Task ExecuteAsync_AllScansSucceed_ReportsNoFailures()
    {
        var handler = CreateHandler(auditSucceeds: true, referenceScanSucceeds: true);

        var result = await handler.ExecuteAsync(AuditOptions());

        result.ScanFailures.Should().Be(0);
        result.DeepScanFailures.Should().Be(0);
        result.ProjectsDiscovered.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AuditQueryFails_ReportsADeepScanFailure()
    {
        // The reference scan succeeded, so the run looks healthy — but `--audit` never returned,
        // meaning "no vulnerabilities" is unknown rather than true.
        var handler = CreateHandler(auditSucceeds: false, referenceScanSucceeds: true);

        var result = await handler.ExecuteAsync(AuditOptions());

        result.DeepScanFailures.Should().Be(1);
        result.ScanFailures.Should().Be(0, "the project's references were read successfully");
    }

    [Fact]
    public async Task ExecuteAsync_ReferenceScanFails_ReportsAScanFailure()
    {
        var handler = CreateHandler(auditSucceeds: true, referenceScanSucceeds: false);

        var result = await handler.ExecuteAsync(AuditOptions());

        result.ScanFailures.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AllScansSucceedWithNoIssues_ExitsSuccess()
    {
        var handler = CreateHandler(auditSucceeds: true, referenceScanSucceeds: true);

        var result = await handler.ExecuteAsync(AuditOptions());

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task ExecuteAsync_AuditQueryFails_ExitsIncompleteAnalysisRatherThanSuccess()
    {
        // Exiting 0 here tells a CI security gate the dependencies are clean when the audit
        // never ran — the failure mode the gate exists to prevent.
        var handler = CreateHandler(auditSucceeds: false, referenceScanSucceeds: true);

        var result = await handler.ExecuteAsync(AuditOptions());

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
    }

    [Fact]
    public async Task ExecuteAsync_ReferenceScanFails_ExitsIncompleteAnalysis()
    {
        var handler = CreateHandler(auditSucceeds: true, referenceScanSucceeds: false);

        var result = await handler.ExecuteAsync(AuditOptions());

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
    }

    [Fact]
    public async Task ExecuteAsync_AuditNotRequested_DoesNotCountAMissingAudit()
    {
        var handler = CreateHandler(auditSucceeds: false, referenceScanSucceeds: true);

        var result = await handler.ExecuteAsync(
            new Options
            {
                Analyze = true,
                Quiet = true,
                SolutionFileDir = _root,
            }
        );

        result.DeepScanFailures.Should().Be(0, "an audit that was never asked for cannot fail");
    }

    private Options AuditOptions()
    {
        return new Options
        {
            Analyze = true,
            AuditSecurity = true,
            Quiet = true,
            SolutionFileDir = _root,
        };
    }

    [Fact]
    public async Task ExecuteAsync_DeclarationScanFails_ReportsAnIncompleteAnalysis()
    {
        // Cross-review caught this, and it is the failure mode this release is about: when the resolved
        // scan succeeds but the project file cannot be read, RedundantReference was not evaluated for that
        // project — yet the run reported a clean, complete result with a success exit code. A consumer
        // parsing the JSON, or a CI job reading the exit code, could not tell that from "nothing found".
        var handler = CreateHandler(
            auditSucceeds: true,
            referenceScanSucceeds: true,
            declarationScanSucceeds: false
        );

        var result = await handler.ExecuteAsync(AuditOptions());

        result.ScanFailures.Should().Be(1, "the project's declarations were never read");
        result
            .ExitCode.Should()
            .Be(
                ExitCodes.IncompleteAnalysis,
                "an unevaluated rule must not be reported as a clean result"
            );
    }

    private AnalysisHandler CreateHandler(
        bool auditSucceeds,
        bool referenceScanSucceeds,
        bool declarationScanSucceeds = true
    )
    {
        var projectAnalyzer = new Mock<IProjectAnalyzer>();

        // The declaration scan feeds the rules that read the project file rather than the
        // resolved graph. Unstubbed it answers "could not read", which is now counted as
        // incomplete coverage.
        projectAnalyzer
            .Setup(a => a.ScanDeclaredPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), declarationScanSucceeds));

        projectAnalyzer
            .Setup(a =>
                a.ScanResolvedPackagesAsync(_projectPath, It.IsAny<bool>(), It.IsAny<string?>())
            )
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

        projectAnalyzer
            .Setup(a => a.ScanVulnerabilitiesAsync(_projectPath))
            .ReturnsAsync((new List<VulnerabilityInfo>(), auditSucceeds));

        return new AnalysisHandler(
            projectAnalyzer.Object,
            new AnalysisService(),
            new FixService(SilentConsoleService.Instance),
            SilentConsoleService.Instance,
            quietMode: true,
            _ => Task.FromResult((_root, new List<string> { _projectPath }))
        );
    }
}
