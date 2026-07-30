using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

/// <summary>
/// The opt-in deep scans — <c>--audit</c>, <c>--outdated</c>, <c>--deprecated</c> — shell out to
/// <c>dotnet package list</c> and therefore restore, exactly as the resolved-package scan does. Two projects
/// sharing a <c>project.assets.json</c> corrupt each other's results, and here that corruption is
/// security-relevant rather than merely wrong.
///
/// Demonstrated before fixing: two projects in one directory, one pinned to Newtonsoft.Json 9.0.1 (a version
/// with a known high-severity advisory) and one to 13.0.1 (clean), queried concurrently for vulnerabilities.
/// The clean project came back reported as carrying 9.0.1. It runs the other way just as easily — which is a
/// genuinely vulnerable project reported clean, and nothing in the output would say so.
///
/// This race has existed since 3.15.0 made those queries concurrent. These tests pin the mechanism that
/// closes it: a deep scan is given the same isolated directory its project's resolved scan proved usable.
/// </summary>
[Collection("Sequential")]
public class DeepScanIsolationTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectPath;

    public DeepScanIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateDeepScan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _projectPath = Path.Combine(_root, "Api.csproj");
        File.WriteAllText(
            _projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """
        );
    }

    public void Dispose()
    {
        ScanConcurrencyGate.ResetForTests();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AVulnerabilityScanIsGivenTheIsolatedDirectoryItsResolvedScanUsed()
    {
        string? resolvedDirectory = null;
        string? auditDirectory = null;

        var analyzer = BuildAnalyzer(
            onResolved: directory =>
            {
                resolvedDirectory = directory;

                // Stand in for a restore that honoured the redirection, so the handler treats this project
                // as isolated and reuses the directory for the deep scan.
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "project.assets.json"), "{}");
                }
            },
            onAudit: directory => auditDirectory = directory
        );

        await CreateHandler(analyzer)
            .ExecuteAsync(
                new Options
                {
                    Analyze = true,
                    AuditSecurity = true,
                    Quiet = true,
                    SolutionFileDir = _root,
                    MaxParallelism = 4,
                }
            );

        resolvedDirectory.Should().NotBeNull("the resolved scan runs concurrently, so it is isolated");
        auditDirectory
            .Should()
            .Be(
                resolvedDirectory,
                "the deep scan must reuse the directory that was proven to hold, or it restores into a "
                    + "location another project may also be writing"
            );
    }

    [Fact]
    public async Task ADeepScanGetsNoIsolationWhenItsResolvedScanEscaped()
    {
        string? auditDirectory = null;

        // No assets file is written to the isolated directory, so the handler sees the restore as having
        // escaped — there is nowhere safe to put the deep scan either, and it must run un-isolated under
        // the exclusive lock rather than into a directory that proved not to hold.
        var analyzer = BuildAnalyzer(
            onResolved: _ => { },
            onAudit: directory => auditDirectory = directory
        );

        await CreateHandler(analyzer)
            .ExecuteAsync(
                new Options
                {
                    Analyze = true,
                    AuditSecurity = true,
                    Quiet = true,
                    SolutionFileDir = _root,
                    MaxParallelism = 4,
                }
            );

        auditDirectory.Should().BeNull();
    }

    private Mock<IProjectAnalyzer> BuildAnalyzer(
        Action<string?> onResolved,
        Action<string?> onAudit
    )
    {
        var analyzer = new Mock<IProjectAnalyzer>();

        analyzer
            .Setup(a => a.ScanDeclaredPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), true));

        analyzer
            .Setup(a =>
                a.ScanResolvedPackagesAsync(_projectPath, It.IsAny<bool>(), It.IsAny<string?>())
            )
            .Callback<string, bool, string?>((_, _, directory) => onResolved(directory))
            .ReturnsAsync(
                (
                    new List<PackageReference>
                    {
                        new("Newtonsoft.Json", "13.0.1", _projectPath, "Api.csproj"),
                    },
                    true
                )
            );

        analyzer
            .Setup(a => a.ScanVulnerabilitiesAsync(_projectPath, It.IsAny<string?>()))
            .Callback<string, string?>((_, directory) => onAudit(directory))
            .ReturnsAsync((new List<VulnerabilityInfo>(), true));

        return analyzer;
    }

    private AnalysisHandler CreateHandler(Mock<IProjectAnalyzer> analyzer)
    {
        return new AnalysisHandler(
            analyzer.Object,
            new AnalysisService(),
            new FixService(SilentConsoleService.Instance),
            new FakeConsoleService(),
            quietMode: true,
            _ => Task.FromResult((_root, new List<string> { _projectPath }))
        );
    }
}
