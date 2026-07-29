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

    private AnalysisHandler CreateHandler(bool auditSucceeds, bool referenceScanSucceeds)
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
