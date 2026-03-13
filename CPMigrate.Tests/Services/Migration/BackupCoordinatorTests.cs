using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

public class BackupCoordinatorTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _consoleService;
    private readonly Mock<IBackupManager> _backupManager;

    public BackupCoordinatorTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateBackupCoordinator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _consoleService = new FakeConsoleService();
        _backupManager = new Mock<IBackupManager>();
    }

    [Fact]
    public void SetupBackupDirectory_DryRun_PrintsPreviewAndReturnsNull()
    {
        var coordinator = CreateCoordinator();
        var request = CreateMigrationRequest(dryRun: true);

        var backupPath = coordinator.SetupBackupDirectory(request);

        backupPath.Should().BeNull();
        _consoleService.OutputMessages.Should().Contain(message => message.Contains("Would create backup directory"));
    }

    [Fact]
    public void SetupBackupDirectory_NonDryRun_CreatesBackupDirectory()
    {
        var coordinator = CreateCoordinator();
        var request = CreateMigrationRequest();

        var backupPath = coordinator.SetupBackupDirectory(request);

        backupPath.Should().NotBeNullOrEmpty();
        Directory.Exists(backupPath!).Should().BeTrue();
        _consoleService.OutputMessages.Should().Contain(message => message.Contains("Backup directory"));
    }

    [Fact]
    public async Task WriteManifestAsync_WritesManifestWhenBackupsExist()
    {
        var coordinator = CreateCoordinator();
        var request = CreateMigrationRequest();
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);
        var backupEntries = new List<BackupEntry>
        {
            new() { OriginalPath = "/tmp/project.csproj", BackupFileName = "project.csproj.backup_1" }
        };

        await coordinator.WriteManifestAsync(
            request,
            backupEntries,
            backupPath,
            propsFilePath: "/tmp/Directory.Packages.props",
            propsFileExisted: true,
            backupTimestamp: "20260313180000000");

        var manifest = await BackupManager.ReadManifestAsync(backupPath);
        manifest.Should().NotBeNull();
        manifest!.Timestamp.Should().Be("20260313180000000");
        manifest.PropsFilePath.Should().Be("/tmp/Directory.Packages.props");
        manifest.PropsFileExisted.Should().BeTrue();
        manifest.Backups.Should().ContainSingle();
    }

    [Fact]
    public void CreatePropsBackup_BacksUpExistingPropsFile()
    {
        var coordinator = CreateCoordinator();
        var request = CreateMigrationRequest();
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);
        File.WriteAllText(propsPath, "<Project />");

        var expectedEntry = new BackupEntry { OriginalPath = propsPath, BackupFileName = "props.backup" };
        _backupManager
            .Setup(manager => manager.CreateBackupForProject(request.Backup, propsPath, backupPath, "stamp"))
            .Returns(expectedEntry);

        var result = coordinator.CreatePropsBackup(request, true, propsPath, backupPath, "stamp");

        result.Should().Be(expectedEntry);
        _consoleService.OutputMessages.Should().Contain("Backed up existing Directory.Packages.props.");
    }

    [Fact]
    public async Task ManageGitIgnoreAsync_HandlesDryRunAndRealWrite()
    {
        var coordinator = CreateCoordinator();
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);

        await coordinator.ManageGitIgnoreAsync(CreateMigrationRequest(dryRun: true, addGitignore: true), backupPath);
        _consoleService.OutputMessages.Should().Contain(message => message.Contains("Would add backup directory"));

        var gitignoreRequest = CreateMigrationRequest(addGitignore: true);
        await coordinator.ManageGitIgnoreAsync(gitignoreRequest, backupPath);

        var gitignorePath = Path.Combine(_testDirectory, ".gitignore");
        File.ReadAllText(gitignorePath).Should().Contain(".cpmigrate_backup/");
    }

    [Fact]
    public void CreateRollbackRequest_NormalizesBackupDirectory()
    {
        var requestFromBackupDir = BackupCoordinator.CreateRollbackRequest(
            new CommandOutput(OutputFormat.Json, Quiet: true, Force: true, OutputFile: null),
            Path.Combine(_testDirectory, ".cpmigrate_backup"),
            fallbackBackupDir: _testDirectory);

        var requestFromResolvedPath = BackupCoordinator.CreateRollbackRequest(
            new CommandOutput(OutputFormat.Terminal, Quiet: false, Force: false, OutputFile: null),
            Path.Combine(_testDirectory, "custom-backups"),
            fallbackBackupDir: _testDirectory);

        requestFromBackupDir.Backup.BackupDir.Should().Be(_testDirectory);
        requestFromBackupDir.Backup.Enabled.Should().BeTrue();
        requestFromBackupDir.Backup.AddBackupToGitignore.Should().BeFalse();
        requestFromResolvedPath.Backup.BackupDir.Should().Be(Path.Combine(_testDirectory, "custom-backups"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private BackupCoordinator CreateCoordinator(bool quietMode = false)
    {
        return new BackupCoordinator(_backupManager.Object, _consoleService, quietMode);
    }

    private MigrationRequest CreateMigrationRequest(bool dryRun = false, bool addGitignore = false)
    {
        return new MigrationRequest(
            DiscoveryTargetPath: _testDirectory,
            ProjectPath: null,
            OutputDir: _testDirectory,
            KeepVersionAttributes: false,
            DryRun: dryRun,
            MergeExisting: true,
            IncludeTransitive: false,
            InteractiveConflicts: false,
            ConflictStrategy: ConflictStrategy.Highest,
            Backup: new BackupSettings(
                Enabled: true,
                BackupDir: _testDirectory,
                AddBackupToGitignore: addGitignore,
                GitignoreDir: _testDirectory),
            Output: new CommandOutput(OutputFormat.Terminal, Quiet: false, Force: false, OutputFile: null));
    }
}
