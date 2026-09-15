using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Models;

public class CommandRequestsTests
{
    [Fact]
    public void FromOptions_MapsRequestsForExplicitProjectWorkflow()
    {
        var options = new Options
        {
            ProjectFileDir = Path.Combine("src", "App", "App.csproj"),
            SolutionFileDir = Path.Combine("src", "App.sln"),
            OutputDir = "artifacts",
            KeepAttributes = true,
            DryRun = true,
            MergeExisting = true,
            IncludeTransitive = true,
            InteractiveConflicts = true,
            ConflictStrategy = ConflictStrategy.Lowest,
            NoBackup = false,
            BackupDir = "backups",
            AddBackupToGitignore = true,
            GitignoreDir = ".",
            Output = OutputFormat.Json,
            OutputFile = "result.json",
            Quiet = true,
            Force = true,
            Analyze = true,
            AuditSecurity = true,
            AnalyzeOutdated = true,
            AnalyzeDeprecated = true,
            AnalyzeLicenses = true,
            IncludePrerelease = true,
            Fix = true,
            BatchDir = "batch-root",
            BatchParallel = true,
            BatchContinue = true,
            UpdatePackages = true
        };

        var backup = BackupSettings.FromOptions(options);
        var output = CommandOutput.FromOptions(options);
        var migration = MigrationRequest.FromOptions(options);
        var analysis = AnalysisRequest.FromOptions(options);
        var fix = FixRequest.FromOptions(options);
        var rollback = RollbackRequest.FromOptions(options);
        var listBackups = ListBackupsRequest.FromOptions(options);
        var packageUpdate = PackageUpdateRequest.FromOptions(options);
        var batch = BatchRequest.FromOptions(options);

        backup.Should().Be(new BackupSettings(true, "backups", true, "."));
        output.Should().Be(new CommandOutput(OutputFormat.Json, true, true, "result.json"));
        output.IsJson.Should().BeTrue();
        output.IsNonInteractive.Should().BeTrue();

        migration.Should().Be(new MigrationRequest(
            DiscoveryTargetPath: options.ProjectFileDir,
            ProjectPath: options.ProjectFileDir,
            OutputDir: "artifacts",
            KeepVersionAttributes: true,
            DryRun: true,
            MergeExisting: true,
            IncludeTransitive: true,
            InteractiveConflicts: true,
            ConflictStrategy: ConflictStrategy.Lowest,
            Backup: backup,
            Output: output));
        migration.HasExplicitProjectPath.Should().BeTrue();

        analysis.Should().Be(new AnalysisRequest(
            DiscoveryTargetPath: options.ProjectFileDir,
            ProjectPath: options.ProjectFileDir,
            IncludeTransitive: true,
            AuditSecurity: true,
            AnalyzeOutdated: true,
            AnalyzeDeprecated: true,
            AnalyzeLicenses: true,
            IncludePrerelease: true,
            Fix: fix,
            Output: output));
        analysis.HasExplicitProjectPath.Should().BeTrue();

        fix.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
        fix.DryRun.Should().BeFalse();
        fix.PropsFilePath.Should().Be(Path.Combine("artifacts", "Directory.Packages.props"));

        rollback.Should().Be(new RollbackRequest(backup, output));
        listBackups.Should().Be(new ListBackupsRequest("backups", output));
        packageUpdate.Should().Be(new PackageUpdateRequest(options.SolutionFileDir, true, true, true, backup, output));
        batch.Should().Be(new BatchRequest("batch-root", true, true, true, true, output));
    }

    [Fact]
    public void FromOptions_UsesDefaultDiscoveryAndOmitsFixWhenNotRequested()
    {
        var options = new Options
        {
            SolutionFileDir = Path.Combine("repo", "App.sln"),
            NoBackup = true,
            BackupDir = "ignored",
            Output = OutputFormat.Terminal,
            Quiet = false,
            Force = false,
            Fix = false,
            FixDryRun = false,
            BatchDir = null
        };

        var migration = MigrationRequest.FromOptions(options);
        var analysis = AnalysisRequest.FromOptions(options);
        var packageUpdate = PackageUpdateRequest.FromOptions(options);
        var batch = BatchRequest.FromOptions(options);
        var output = CommandOutput.FromOptions(options);

        migration.DiscoveryTargetPath.Should().Be(options.SolutionFileDir);
        migration.ProjectPath.Should().BeNull();
        migration.Backup.Enabled.Should().BeFalse();
        migration.Output.Should().Be(output);
        migration.HasExplicitProjectPath.Should().BeFalse();

        analysis.DiscoveryTargetPath.Should().Be(options.SolutionFileDir);
        analysis.ProjectPath.Should().BeNull();
        analysis.Fix.Should().BeNull();
        analysis.HasExplicitProjectPath.Should().BeFalse();

        packageUpdate.Backup.Enabled.Should().BeFalse();
        batch.BatchDir.Should().BeEmpty();
        batch.Output.Should().Be(output);
        output.IsJson.Should().BeFalse();
        output.IsNonInteractive.Should().BeFalse();
    }

    [Fact]
    public void PackageUpdateRequest_OmittedSolutionPath_DefaultsToCurrentDirectory()
    {
        // -s documents "current directory when omitted". The raw default is an empty string,
        // which Path.GetFullPath rejects downstream — the request must carry the default applied.
        var request = PackageUpdateRequest.FromOptions(new Options());

        request.SolutionPath.Should().Be(".");
        Path.GetFullPath(request.SolutionPath).Should().Be(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void PackageUpdateRequest_ExplicitSolutionPath_IsPreserved()
    {
        var options = new Options { SolutionFileDir = Path.Combine("repo", "App.sln") };

        PackageUpdateRequest.FromOptions(options).SolutionPath.Should().Be(options.SolutionFileDir);
    }

    [Fact]
    public void FromOptions_MapsDiffFlagsOntoTheFixRequest()
    {
        var options = new Options
        {
            Analyze = true,
            FixDryRun = true,
            Diff = true,
            DiffFile = "diffs.patch",
        };

        var fix = FixRequest.FromOptions(options);

        fix.DryRun.Should().BeTrue();
        fix.ShowDiff.Should().BeTrue();
        fix.DiffFilePath.Should().Be("diffs.patch");
    }

    [Fact]
    public void FromOptions_MapsDiffFlagsOntoThePackageUpdateRequest()
    {
        var options = new Options
        {
            UpdatePackages = true,
            DryRun = true,
            Diff = true,
            DiffFile = "updates.patch",
        };

        var request = PackageUpdateRequest.FromOptions(options);

        request.DryRun.Should().BeTrue();
        request.ShowDiff.Should().BeTrue();
        request.DiffFilePath.Should().Be("updates.patch");
    }

    [Fact]
    public void FromOptions_MapsDiffFlagsOntoTheRemediateRequest()
    {
        var options = new Options
        {
            Remediate = true,
            DryRun = true,
            Diff = true,
            DiffFile = "remediate.patch",
        };

        var request = RemediateRequest.FromOptions(options);

        request.DryRun.Should().BeTrue();
        request.ShowDiff.Should().BeTrue();
        request.DiffFilePath.Should().Be("remediate.patch");
    }

    [Fact]
    public void FixRequest_DryRunWrite_InvokesPlannedWriteSink_WithoutTouchingDisk()
    {
        var dir = Directory.CreateTempSubdirectory("cpmigrate-fixreq").FullName;
        try
        {
            var target = Path.Combine(dir, "App.csproj");
            File.WriteAllText(target, "original");
            var recorded = new List<KeyValuePair<string, string>>();
            var request = new FixRequest("props.props", ConflictStrategy.Highest, DryRun: true) with
            {
                OnPlannedWrite = (path, contents) => recorded.Add(new(path, contents)),
            };

            request.WriteFile(target, "changed");

            File.ReadAllText(target).Should().Be("original");
            recorded.Should().ContainSingle();
            recorded[0].Key.Should().Be(target);
            recorded[0].Value.Should().Be("changed");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FixRequest_ReadFile_ConsultsTheOverlay_AndFallsBackToDisk()
    {
        var dir = Directory.CreateTempSubdirectory("cpmigrate-fixreq").FullName;
        try
        {
            var target = Path.Combine(dir, "App.csproj");
            var other = Path.Combine(dir, "Lib.csproj");
            File.WriteAllText(target, "on disk");
            File.WriteAllText(other, "also on disk");
            var request = new FixRequest("props.props", ConflictStrategy.Highest, DryRun: true) with
            {
                PlannedRead = path => path == target ? "planned" : null,
            };

            request.ReadFile(target).Should().Be("planned");
            request.ReadFile(other).Should().Be("also on disk", "an unplanned path reads disk");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FixRequest_ReadFile_WithoutOverlay_ReadsFromDisk()
    {
        var dir = Directory.CreateTempSubdirectory("cpmigrate-fixreq").FullName;
        try
        {
            var target = Path.Combine(dir, "App.csproj");
            File.WriteAllText(target, "on disk");

            var dryRun = new FixRequest("props.props", ConflictStrategy.Highest, DryRun: true);
            var realRun = new FixRequest("props.props", ConflictStrategy.Highest, DryRun: false);

            dryRun.ReadFile(target).Should().Be("on disk");
            realRun.ReadFile(target).Should().Be("on disk");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FixRequest_WriteFile_WritesAtomically_LeavingNoTempBehind()
    {
        // Fixer output is a user's project file: the write goes through FileHelper.WriteAtomic so
        // an interrupted run never leaves a truncated .csproj — the target is only ever replaced
        // by a fully-written file, and no temp file survives a successful one.
        var dir = Directory.CreateTempSubdirectory("cpmigrate-fixreq").FullName;
        try
        {
            var target = Path.Combine(dir, "App.csproj");
            File.WriteAllText(target, "original");
            var request = new FixRequest("props.props", ConflictStrategy.Highest, DryRun: false);

            request.WriteFile(target, "changed");

            File.ReadAllText(target).Should().Be("changed");
            Directory.GetFiles(dir, "*.tmp.*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FixRequest_WriteFile_ToUnwritableTarget_ThrowsFixWriteException()
    {
        // The atomic path still surfaces failures as FixWriteException, the contract fixers rely on.
        var dir = Directory.CreateTempSubdirectory("cpmigrate-fixreq").FullName;
        try
        {
            var blockingFile = Path.Combine(dir, "blocker");
            File.WriteAllText(blockingFile, "untouched");
            var request = new FixRequest("props.props", ConflictStrategy.Highest, DryRun: false);

            var act = () => request.WriteFile(Path.Combine(blockingFile, "nested.csproj"), "x");

            act.Should().Throw<FixWriteException>();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

}
