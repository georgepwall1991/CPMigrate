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
}
