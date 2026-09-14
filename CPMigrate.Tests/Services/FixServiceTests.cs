using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for FixService covering fix application workflow,
/// dry-run mode, error handling, and reporting.
/// </summary>
public class FixServiceTests
{
    private readonly FakeConsoleService _console;
    private readonly FixService _fixService;

    public FixServiceTests()
    {
        _console = new FakeConsoleService();
        _fixService = new FixService(_console);
    }

    [Fact]
    public void ApplyFixes_NoIssues_ReturnsSuccessWithNoChanges()
    {
        // Arrange
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 0,
            Results: Array.Empty<AnalyzerResult>()
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.Should().NotBeNull();
        fixReport.TotalFixesApplied.Should().Be(0);
        fixReport.TotalFileChanges.Should().Be(0);
        fixReport.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void ApplyFixes_ReportWithNoIssuesInResults_ReturnsSuccessWithNoChanges()
    {
        // Arrange
        var analyzerResults = new List<AnalyzerResult>
        {
            new("Version Inconsistencies", Array.Empty<AnalysisIssue>())
        };
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 5,
            Results: analyzerResults
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.TotalFixesApplied.Should().Be(0);
        fixReport.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void ApplyFixes_WithVersionInconsistency_InvokesFixers()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var project1Path = Path.Combine(testDir, "Project1.csproj");
            var project2Path = Path.Combine(testDir, "Project2.csproj");

            // Create test projects with same package, different versions
            var content1 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.1"" />
  </ItemGroup>
</Project>";
            var content2 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
            File.WriteAllText(project1Path, content1);
            File.WriteAllText(project2Path, content2);

            var issues = new List<AnalysisIssue>
            {
                new("Newtonsoft.Json",
                    "12.0.1 (Project1), 13.0.1 (Project2)",
                    new[] { project1Path, project2Path },
                    AnalysisIssueCode.VersionInconsistency)
            };
            var analyzerResults = new List<AnalyzerResult>
            {
                new("Version Inconsistencies", issues)
            };
            var report = new AnalysisReport(
                ProjectsScanned: 2,
                TotalPackageReferences: 2,
                Results: analyzerResults
            );

            var packageInfo = new ProjectPackageInfo(new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
                new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
            });

            var options = new Options { ConflictStrategy = ConflictStrategy.Highest, BackupDir = testDir };

            // Act
            var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

            // Assert
            fixReport.Should().NotBeNull();
            fixReport.Results.Should().HaveCount(1);
            var result = fixReport.Results[0];
            result.Success.Should().BeTrue();
            result.Changes.Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyFixes_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var project1Path = Path.Combine(testDir, "Project1.csproj");
            var originalContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.1"" />
  </ItemGroup>
</Project>";
            File.WriteAllText(project1Path, originalContent);

            var issues = new List<AnalysisIssue>
            {
                new("Newtonsoft.Json",
                    "Version inconsistency",
                    new[] { project1Path },
                    AnalysisIssueCode.VersionInconsistency)
            };
            var analyzerResults = new List<AnalyzerResult>
            {
                new("Version Inconsistencies", issues)
            };
            var report = new AnalysisReport(
                ProjectsScanned: 1,
                TotalPackageReferences: 1,
                Results: analyzerResults
            );

            var packageInfo = new ProjectPackageInfo(new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj")
            });

            var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

            // Act
            var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: true);

            // Assert
            fixReport.Should().NotBeNull();
            // In dry-run, file should not be modified
            File.ReadAllText(project1Path).Should().Be(originalContent);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyFixes_NoFixerAvailable_ContinuesWithoutThrowing()
    {
        // Arrange
        // Create an issue with a description that doesn't match any fixer pattern
        var issues = new List<AnalysisIssue>
        {
            new("UnknownPackage", "Some unknown issue type", new[] { "Project1.csproj" })
        };
        var analyzerResults = new List<AnalyzerResult>
        {
            new("Unknown Analyzer", issues)
        };
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 1,
            Results: analyzerResults
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.Should().NotBeNull();
        fixReport.TotalFixesApplied.Should().Be(0);
        // Service should not throw, just skip unfixable issues
    }

    [Fact]
    public void ApplyFixes_MultipleIssues_ProcessesAll()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var project1Path = Path.Combine(testDir, "Project1.csproj");
            var project2Path = Path.Combine(testDir, "Project2.csproj");

            var content1 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.1"" />
    <PackageReference Include=""Serilog"" Version=""2.10.0"" />
  </ItemGroup>
</Project>";
            var content2 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""2.11.0"" />
  </ItemGroup>
</Project>";
            File.WriteAllText(project1Path, content1);
            File.WriteAllText(project2Path, content2);

            var issues = new List<AnalysisIssue>
            {
                new("Newtonsoft.Json",
                    "12.0.1 (Project1), 13.0.1 (Project2)",
                    new[] { project1Path, project2Path },
                    AnalysisIssueCode.VersionInconsistency),
                new("Serilog",
                    "2.10.0 (Project1), 2.11.0 (Project2)",
                    new[] { project1Path, project2Path },
                    AnalysisIssueCode.VersionInconsistency)
            };
            var analyzerResults = new List<AnalyzerResult>
            {
                new("Version Inconsistencies", issues)
            };
            var report = new AnalysisReport(
                ProjectsScanned: 2,
                TotalPackageReferences: 4,
                Results: analyzerResults
            );

            var packageInfo = new ProjectPackageInfo(new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
                new("Serilog", "2.10.0", project1Path, "Project1.csproj"),
                new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj"),
                new("Serilog", "2.11.0", project2Path, "Project2.csproj")
            });

            var options = new Options { ConflictStrategy = ConflictStrategy.Highest, BackupDir = testDir };

            // Act
            var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

            // Assert
            fixReport.Should().NotBeNull();
            fixReport.Results.Should().HaveCount(2); // Two issues fixed
            fixReport.TotalFixesApplied.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyFixes_ReportHasIssuesFalse_ReturnsEarly()
    {
        // Arrange
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 0,
            Results: Array.Empty<AnalyzerResult>()
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.Should().NotBeNull();
        fixReport.Results.Should().BeEmpty();
        report.HasIssues.Should().BeFalse();
    }

    [Fact]
    public void ApplyFixes_FixerReturnsFailure_AddsFailedResult()
    {
        // Arrange
        var issue = new AnalysisIssue("Pkg", "desc", new[] { "Project.csproj" }, AnalysisIssueCode.VersionInconsistency);
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 1,
            Results: new[] { new AnalyzerResult("Test", new[] { issue }) });
        var console = new FakeConsoleService();
        var failingFixer = new StubFixer(canFix: _ => true, fix: (_, _, _) => FixResult.Failed("failed"));
        var fixService = new FixService(console, new[] { failingFixer });

        // Act
        var fixReport = fixService.ApplyFixes(report, new ProjectPackageInfo(Array.Empty<PackageReference>()), new FixRequest("props.props", ConflictStrategy.Highest, false));

        // Assert
        fixReport.Results.Should().ContainSingle(r => !r.Success);
        console.ErrorMessages.Should().ContainMatch("*Failed to fix Pkg*");
    }

    [Fact]
    public void ApplyFixes_FixerThrowsException_AddsFailedResultAndContinues()
    {
        // Arrange
        var issue = new AnalysisIssue("Pkg", "desc", new[] { "Project.csproj" }, AnalysisIssueCode.VersionInconsistency);
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 1,
            Results: new[] { new AnalyzerResult("Test", new[] { issue }) });
        var console = new FakeConsoleService();
        var throwingFixer = new StubFixer(canFix: _ => true, fix: (_, _, _) => throw new InvalidOperationException("boom"));
        var fixService = new FixService(console, new[] { throwingFixer });

        // Act
        var fixReport = fixService.ApplyFixes(report, new ProjectPackageInfo(Array.Empty<PackageReference>()), new FixRequest("props.props", ConflictStrategy.Highest, false));

        // Assert
        fixReport.Results.Should().ContainSingle(r => !r.Success);
        console.ErrorMessages.Should().ContainMatch("*Error fixing Pkg*");
    }

    [Fact]
    public void ApplyFixes_FixerSucceedsWithNoChanges_DoesNotWriteFixResult()
    {
        // Arrange
        var issue = new AnalysisIssue("Pkg", "desc", new[] { "Project.csproj" }, AnalysisIssueCode.VersionInconsistency);
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 1,
            Results: new[] { new AnalyzerResult("Test", new[] { issue }) });
        var console = new FakeConsoleService();
        var noChangeFixer = new StubFixer(canFix: _ => true, fix: (_, _, _) => FixResult.Succeeded("already ok", Array.Empty<FileChange>()));
        var fixService = new FixService(console, new[] { noChangeFixer });

        // Act
        fixService.ApplyFixes(report, new ProjectPackageInfo(Array.Empty<PackageReference>()), new FixRequest("props.props", ConflictStrategy.Highest, false));

        // Assert
        console.OutputMessages.Should().NotContainMatch("*Fixed*");
    }

    [Fact]
    public void ApplyFixes_FixerThrowsFixWriteException_ReportsTheCause()
    {
        // A file that could not be changed must be reported with its cause — "access denied" —
        // not as a generic fixing error and never as "no changes needed".
        var issue = new AnalysisIssue("Pkg", "desc", new[] { "Project.csproj" }, AnalysisIssueCode.VersionInconsistency);
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 1,
            Results: new[] { new AnalyzerResult("Test", new[] { issue }) });
        var console = new FakeConsoleService();
        var lockedFixer = new StubFixer(canFix: _ => true, fix: (_, _, _) => throw new FixWriteException(
            Path.Combine("some", "dir", "App.csproj"),
            new UnauthorizedAccessException("Access to the path is denied.")));
        var fixService = new FixService(console, new[] { lockedFixer });

        var fixReport = fixService.ApplyFixes(report, new ProjectPackageInfo(Array.Empty<PackageReference>()), new FixRequest("props.props", ConflictStrategy.Highest, false));

        fixReport.Results.Should().ContainSingle(r => !r.Success);
        fixReport.Results.Should().ContainSingle(r => r.Description.Contains("Could not modify App.csproj"));
        console.ErrorMessages.Should().ContainMatch("*Could not fix Pkg*Access to the path is denied*");
    }

    [Fact]
    public void ApplyFixes_OnlyRules_SkipsOtherRules()
    {
        // A --fix-rule restriction narrows the pass to the named rules — a user who wants only
        // OrphanedPackageVersion fixed should not have to accept every other fixable finding's
        // edit in the same pass.
        var console = new FakeConsoleService();
        var fixer = new StubFixer(
            _ => true,
            (issue, _, _) => FixResult.Succeeded($"fixed {issue.PackageName}", [])
        );
        var fixService = new FixService(console, new[] { fixer });

        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 2,
            Results: new List<AnalyzerResult>
            {
                new("Test", new List<AnalysisIssue>
                {
                    new("PkgA", "orphaned", new[] { "App.csproj" }, AnalysisIssueCode.OrphanedPackageVersion, AnalysisSeverity.Low, Fixable: true),
                    new("PkgB", "inline version", new[] { "App.csproj" }, AnalysisIssueCode.InlineVersionUnderCpm, AnalysisSeverity.Moderate, Fixable: true),
                })
            }
        );

        var fixReport = fixService.ApplyFixes(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>()),
            new FixRequest("props.props", ConflictStrategy.Highest, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OrphanedPackageVersion" })
        );

        fixReport.Results.Should().ContainSingle(r => r.Description.Contains("PkgA"));
        fixReport.Results.Should().NotContain(r => r.Description.Contains("PkgB"));
    }

    [Fact]
    public void ApplyFixes_StampsIssueIdentityOnResult()
    {
        // A consumer reading the JSON document needs the rule ID and package to correlate a fix
        // back to the analysisIssues entry it resolved — the fixer does not know which finding it
        // was dispatched against, so the service stamps both before the result reaches the report.
        var console = new FakeConsoleService();
        var fixer = new StubFixer(
            _ => true,
            (issue, _, _) => FixResult.Succeeded($"fixed {issue.PackageName}", [])
        );
        var fixService = new FixService(console, new[] { fixer });

        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 1,
            Results: new List<AnalyzerResult>
            {
                new("Test", new List<AnalysisIssue>
                {
                    new("Serilog", "orphaned", new[] { "App.csproj" }, AnalysisIssueCode.OrphanedPackageVersion, AnalysisSeverity.Low, Fixable: true),
                })
            }
        );

        var fixReport = fixService.ApplyFixes(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>()),
            new FixRequest("props.props", ConflictStrategy.Highest, false)
        );

        fixReport.Results.Should().ContainSingle();
        fixReport.Results[0].IssueCode.Should().Be("OrphanedPackageVersion");
        fixReport.Results[0].PackageName.Should().Be("Serilog");
    }

    [Fact]
    public async Task ApplyFixes_WritesAreBackedUpAndManifested()
    {
        // A fix pass rewrites files exactly like a migration does, so it owes the same undo path:
        // every file lands in .cpmigrate_backup before its first write, and the manifest is the one
        // --rollback already restores from.
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixBackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var target = Path.Combine(testDir, "App.csproj");
            const string original = "<Project />";
            File.WriteAllText(target, original);

            var propsPath = Path.Combine(testDir, "Directory.Packages.props");
            var fixReport = RunWritingPass(
                testDir,
                propsPath,
                request =>
                {
                    request.WriteFile(target, "<Project><ItemGroup /></Project>");
                    return FixResult.Succeeded("fixed", [new FileChange(target, "Modified", "a", "b")]);
                },
                backupDir: ".");

            fixReport.TotalFixesApplied.Should().Be(1);

            // The default backup dir anchored at the props file's directory, not the test host cwd.
            var backupDir = Path.Combine(testDir, ".cpmigrate_backup");
            Directory.Exists(backupDir).Should().BeTrue();

            var manifest = await BackupManager.ReadManifestAsync(backupDir);
            manifest.Should().NotBeNull();
            manifest!.PropsFilePath.Should().Be(propsPath);
            manifest.PropsFileExisted.Should().BeFalse();
            manifest.Backups.Should().ContainSingle(e => e.OriginalPath == Path.GetFullPath(target));

            var backupCopy = await File.ReadAllTextAsync(
                Path.Combine(backupDir, manifest.Backups[0].BackupFileName));
            backupCopy.Should().Be(original);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyFixes_SameFileWrittenTwice_IsBackedUpOnce()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixBackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var target = Path.Combine(testDir, "App.csproj");
            const string original = "<Project />";
            File.WriteAllText(target, original);

            RunWritingPass(
                testDir,
                Path.Combine(testDir, "Directory.Packages.props"),
                request =>
                {
                    request.WriteFile(target, File.ReadAllText(target) + " ");
                    return FixResult.Succeeded("fixed", [new FileChange(target, "Modified", "a", "b")]);
                },
                issueCount: 2);

            var backupDir = Path.Combine(testDir, ".cpmigrate_backup");
            var manifest = await BackupManager.ReadManifestAsync(backupDir);
            manifest!.Backups.Should().ContainSingle();
            (await File.ReadAllTextAsync(Path.Combine(backupDir, manifest.Backups[0].BackupFileName)))
                .Should().Be(original);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyFixes_DryRun_CreatesNoBackupDirectory()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixBackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var target = Path.Combine(testDir, "App.csproj");
            const string original = "<Project />";
            File.WriteAllText(target, original);

            RunWritingPass(
                testDir,
                Path.Combine(testDir, "Directory.Packages.props"),
                request =>
                {
                    request.WriteFile(target, "changed");
                    return FixResult.Succeeded("fixed", [new FileChange(target, "Modified", "a", "b")]);
                },
                dryRun: true);

            Directory.Exists(Path.Combine(testDir, ".cpmigrate_backup")).Should().BeFalse();
            File.ReadAllText(target).Should().Be(original);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyFixes_BackupDisabled_CreatesNoBackupDirectory()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixBackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var target = Path.Combine(testDir, "App.csproj");
            File.WriteAllText(target, "<Project />");

            RunWritingPass(
                testDir,
                Path.Combine(testDir, "Directory.Packages.props"),
                request =>
                {
                    request.WriteFile(target, "changed");
                    return FixResult.Succeeded("fixed", [new FileChange(target, "Modified", "a", "b")]);
                },
                backupEnabled: false);

            File.ReadAllText(target).Should().Be("changed");
            Directory.Exists(Path.Combine(testDir, ".cpmigrate_backup")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyFixes_NewFileCreation_DoesNotFailBackup()
    {
        // A fixer may create a file that did not exist (e.g. the props file itself). There is
        // nothing to back up — the manifest's PropsFileExisted=false is what makes --rollback
        // delete it again — and the write must go through rather than die on a missing source.
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixBackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var propsPath = Path.Combine(testDir, "Directory.Packages.props");

            var fixReport = RunWritingPass(
                testDir,
                propsPath,
                request =>
                {
                    request.WriteFile(propsPath, "<Project />");
                    return FixResult.Succeeded("created", [new FileChange(propsPath, "Created", "", "x")]);
                });

            fixReport.TotalFixesApplied.Should().Be(1);
            File.Exists(propsPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Runs a one-analyzer fix pass whose fixer writes through <see cref="FixRequest.WriteFile"/>,
    /// the way the real fixers now do. Backup settings default to enabled with the backup anchored
    /// inside the fixture directory.
    /// </summary>
    private FixReport RunWritingPass(
        string testDir,
        string propsPath,
        Func<FixRequest, FixResult> fix,
        int issueCount = 1,
        bool dryRun = false,
        bool backupEnabled = true,
        string? backupDir = null)
    {
        var issues = Enumerable
            .Range(0, issueCount)
            .Select(i => new AnalysisIssue(
                $"Pkg{i}", "d", new[] { "App.csproj" }, AnalysisIssueCode.VersionInconsistency))
            .Cast<AnalysisIssue>()
            .ToList();
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: issueCount,
            Results: new[] { new AnalyzerResult("Test", issues) });

        var writingFixer = new StubFixer(_ => true, (_, _, request) => fix(request));
        var fixService = new FixService(_console, new[] { writingFixer });

        return fixService.ApplyFixes(
            report,
            new ProjectPackageInfo(Array.Empty<PackageReference>()),
            new FixRequest(
                propsPath,
                ConflictStrategy.Highest,
                DryRun: dryRun,
                Backup: new BackupSettings(backupEnabled, backupDir ?? testDir, false, testDir)));
    }

    private sealed class StubFixer : IFixer
    {
        private readonly Func<AnalysisIssue, bool> _canFix;
        private readonly Func<AnalysisIssue, ProjectPackageInfo, FixRequest, FixResult> _fix;

        public StubFixer(Func<AnalysisIssue, bool> canFix, Func<AnalysisIssue, ProjectPackageInfo, FixRequest, FixResult> fix)
        {
            _canFix = canFix;
            _fix = fix;
        }

        public string Name => "Stub";

        public bool CanFix(AnalysisIssue issue) => _canFix(issue);

        public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun) =>
            Fix(issue, packageInfo, new FixRequest("", options.ConflictStrategy, dryRun));

        public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request) => _fix(issue, packageInfo, request);
    }
}
