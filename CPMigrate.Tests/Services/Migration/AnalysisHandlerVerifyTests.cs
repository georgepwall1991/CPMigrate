using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Services.Verify;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

/// <summary>
/// <c>--analyze --fix --verify</c>: the fix pass gets the same restore-graph proof a migration
/// gets — baseline before a byte is written, re-restore after, and a broken tree is rolled back
/// from the backup the pass itself made.
/// </summary>
public class AnalysisHandlerVerifyTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectPath;
    private readonly string _propsPath;

    public AnalysisHandlerVerifyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateVerifyFix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _projectPath = Path.Combine(_root, "App.csproj");
        _propsPath = Path.Combine(_root, "Directory.Packages.props");
        File.WriteAllText(
            _projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                + "<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />"
                + "</ItemGroup></Project>"
        );
        File.WriteAllText(
            _propsPath,
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
                + "</PropertyGroup><ItemGroup>"
                + "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />"
                + "</ItemGroup></Project>"
        );
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Verify_RestoreBrokenAfterFix_RollsBackAndExitsGraphDrift()
    {
        var original = await File.ReadAllTextAsync(_projectPath);
        var (handler, _) = CreateHandler(
            baselineOk: true,
            afterOk: false
        );

        var result = await handler.ExecuteAsync(FixVerifyOptions());

        result.Verification.Should().NotBeNull();
        result.Verification!.Verdict.Should().Be(VerificationVerdict.Failed);
        result.Verification.RolledBack.Should().BeTrue();
        result.ExitCode.Should().Be(ExitCodes.GraphDrift);
        (await File.ReadAllTextAsync(_projectPath)).Should().Be(original);
    }

    [Fact]
    public async Task Verify_BaselineDoesNotRestore_WritesNothing()
    {
        var original = await File.ReadAllTextAsync(_projectPath);
        var (handler, fixer) = CreateHandler(baselineOk: false, afterOk: false);

        var result = await handler.ExecuteAsync(FixVerifyOptions());

        result.Verification.Should().NotBeNull();
        result.Verification!.Verdict.Should().Be(VerificationVerdict.Failed);
        result.ExitCode.Should().Be(ExitCodes.GraphDrift);
        result.FixReport.Should().BeNull("no fix may run against a tree that does not restore");
        fixer.WriteCount.Should().Be(0);
        (await File.ReadAllTextAsync(_projectPath)).Should().Be(original);
    }

    [Fact]
    public async Task Verify_GraphUnchanged_Passes()
    {
        var (handler, _) = CreateHandler(baselineOk: true, afterOk: true);

        var result = await handler.ExecuteAsync(FixVerifyOptions());

        result.Verification.Should().NotBeNull();
        result.Verification!.Verdict.Should().Be(VerificationVerdict.Unchanged);
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task Verify_GraphMovedByFix_IsExplainedDrift_AndPassesNonStrict()
    {
        // A fix that moves a resolved version is working as intended — the drift is attributed to
        // the fix pass, not treated as unexplained. Only --verify-strict may fail it.
        var (handler, _) = CreateHandler(
            baselineOk: true,
            afterOk: true,
            afterVersion: "13.0.1"
        );

        var result = await handler.ExecuteAsync(FixVerifyOptions());

        result.Verification.Should().NotBeNull();
        result.Verification!.Verdict.Should().Be(VerificationVerdict.ExplainedDrift);
        result.Verification.Changes.Should().OnlyContain(c => c.Kind == DriftExplanation.FixApplied);
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task Verify_GraphMovedWhereNoFixRan_RollsBackAsUnexplained()
    {
        // "Serilog" moved but the fix report only claims Newtonsoft.Json — a fix run has no more
        // right to move a package it never touched than a migration does.
        var original = await File.ReadAllTextAsync(_projectPath);
        var (handler, _) = CreateHandler(
            baselineOk: true,
            afterOk: true,
            afterPackageId: "Serilog"
        );

        var result = await handler.ExecuteAsync(FixVerifyOptions());

        result.Verification.Should().NotBeNull();
        result.Verification!.Verdict.Should().Be(VerificationVerdict.UnexplainedDrift);
        result.Verification.RolledBack.Should().BeTrue();
        result.ExitCode.Should().Be(ExitCodes.GraphDrift);
        (await File.ReadAllTextAsync(_projectPath)).Should().Be(original);
    }

    [Fact]
    public async Task Verify_GraphMovedByFix_FailsUnderStrict_WithoutRollingBack()
    {
        var (handler, _) = CreateHandler(
            baselineOk: true,
            afterOk: true,
            afterVersion: "13.0.1"
        );

        var options = FixVerifyOptions();
        options.VerifyStrict = true;
        var result = await handler.ExecuteAsync(options);

        result.Verification.Should().NotBeNull();
        result.Verification!.Verdict.Should().Be(VerificationVerdict.ExplainedDrift);
        result.Verification.RolledBack.Should().BeFalse(
            "explained drift is left in place for inspection, the same as a strict migration"
        );
        result.ExitCode.Should().Be(ExitCodes.GraphDrift);
    }

    private Options FixVerifyOptions() =>
        new()
        {
            Analyze = true,
            Fix = true,
            Verify = true,
            SolutionFileDir = _root,
            BackupDir = _root,
            Quiet = true,
        };

    private (AnalysisHandler Handler, RecordingFixer Fixer) CreateHandler(
        bool baselineOk,
        bool afterOk,
        string afterVersion = "12.0.1",
        string afterPackageId = "Newtonsoft.Json")
    {
        var projectAnalyzer = new Mock<IProjectAnalyzer>();
        projectAnalyzer
            .Setup(a => a.ScanDeclaredPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), true));
        projectAnalyzer
            .Setup(a => a.ScanResolvedPackagesAsync(_projectPath, It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(
                (
                    new List<PackageReference>
                    {
                        new("Newtonsoft.Json", "12.0.1", _projectPath, "App.csproj"),
                    },
                    true
                )
            );
        projectAnalyzer
            .Setup(a => a.ScanProjectPackages(_projectPath))
            .Returns((new List<PackageReference>(), true));

        var fixer = new RecordingFixer();
        var fixService = new FixService(SilentConsoleService.Instance, [fixer]);

        var baseline = Snapshot(_projectPath, "Newtonsoft.Json", "12.0.1", baselineOk);
        var after = Snapshot(_projectPath, afterPackageId, afterVersion, afterOk);
        var verifier = new MigrationVerifier(new StubSnapshotService(baseline, after));

        var handler = new AnalysisHandler(
            projectAnalyzer.Object,
            new StubAnalysisService(
                new AnalysisIssue(
                    "Newtonsoft.Json",
                    "inline version",
                    new[] { _projectPath },
                    AnalysisIssueCode.InlineVersionUnderCpm,
                    AnalysisSeverity.Low,
                    Fixable: true
                )
            ),
            fixService,
            SilentConsoleService.Instance,
            quietMode: true,
            _ => Task.FromResult((_root, new List<string> { _projectPath })),
            verifier: verifier,
            rollbackHandler: new RollbackHandler(SilentConsoleService.Instance, quietMode: true)
        );
        return (handler, fixer);
    }

    private static GraphSnapshotResult Snapshot(
        string projectPath,
        string packageId,
        string version,
        bool restoreSucceeded) =>
        new(
            restoreSucceeded,
            restoreSucceeded ? "restore ok" : "error NU1101: package not found",
            new ResolvedGraphSnapshot(
                restoreSucceeded
                    ?
                    [
                        new ProjectResolvedGraph(
                            projectPath,
                            [new ResolvedFramework("net10.0", true, [new ResolvedPackage(packageId, version, true)])]
                        ),
                    ]
                    : [],
                []
            )
        );

    /// <summary>Answers the two captures a verify pass makes, in order.</summary>
    private sealed class StubSnapshotService(GraphSnapshotResult baseline, GraphSnapshotResult after)
        : IGraphSnapshotService
    {
        private int _calls;

        public Task<GraphSnapshotResult> CaptureAsync(
            string restoreTargetPath,
            IReadOnlyList<string> projectPaths,
            string? basePath) =>
            Task.FromResult(_calls++ == 0 ? baseline : after);
    }

    /// <summary>
    /// A fixer that really writes — through <see cref="FixRequest.WriteFile"/>, so the pass's backup
    /// session sees the write exactly as a shipping fixer's would be seen.
    /// </summary>
    private sealed class RecordingFixer : IFixer
    {
        public string Name => "RecordingFixer";

        public int WriteCount { get; private set; }

        public bool CanFix(AnalysisIssue issue) => issue.Fixable;

        public FixResult Fix(
            AnalysisIssue issue,
            ProjectPackageInfo packageInfo,
            Options options,
            bool dryRun) =>
            Fix(issue, packageInfo, FixRequest.FromOptions(options));

        public FixResult Fix(
            AnalysisIssue issue,
            ProjectPackageInfo packageInfo,
            FixRequest request)
        {
            var projectPath = issue.AffectedProjects.FirstOrDefault(File.Exists);
            if (projectPath is null)
            {
                return FixResult.Failed("no affected file on disk");
            }

            request.WriteFile(projectPath, File.ReadAllText(projectPath) + " ");
            WriteCount++;
            return FixResult.Succeeded(
                $"fixed {issue.PackageName}",
                [new FileChange(projectPath, "Modified", "", "")]
            );
        }
    }

    /// <summary>
    /// The finding exists on the first scan — that is what makes the run fix — and is gone on the
    /// post-fix rescan, the state a successful fix is supposed to leave behind.
    /// </summary>
    private sealed class StubAnalysisService(AnalysisIssue issue) : IAnalysisService
    {
        private int _calls;

        public AnalysisReport Analyze(ProjectPackageInfo packageInfo) =>
            new(
                packageInfo.ProjectCount,
                packageInfo.TotalReferences,
                [new AnalyzerResult("Stub", _calls++ == 0 ? [issue] : [])]
            );
    }
}
