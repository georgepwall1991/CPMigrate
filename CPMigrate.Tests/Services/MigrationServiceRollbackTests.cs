using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class MigrationServiceRollbackTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly BackupManager _backupManager;
    private readonly FakeConsoleService _console;

    public MigrationServiceRollbackTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateRollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _backupManager = new BackupManager();
        _console = new FakeConsoleService { ConfirmationResponse = true };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RollbackPreservesExistingPropsFile()
    {
        var (projectPath, propsFilePath, _) = await CreateRollbackScenarioAsync(propsFileExisted: true);

        var service = new MigrationService(_console, backupManager: _backupManager);
        var options = new Options
        {
            Rollback = true,
            BackupDir = _testDirectory
        };

        var result = await service.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        File.Exists(propsFilePath).Should().BeTrue();
        File.ReadAllText(projectPath).Should().Be("original");
    }

    [Fact]
    public async Task ExecuteAsync_RollbackDeletesPropsFileWhenCreatedByMigration()
    {
        var (projectPath, propsFilePath, _) = await CreateRollbackScenarioAsync(propsFileExisted: false);

        var service = new MigrationService(_console, backupManager: _backupManager);
        var options = new Options
        {
            Rollback = true,
            BackupDir = _testDirectory
        };

        var result = await service.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        File.Exists(propsFilePath).Should().BeFalse();
        File.ReadAllText(projectPath).Should().Be("original");
    }

    [Fact]
    public async Task ExecuteAsync_RollbackRestoresMergedPropsFileFromBackup()
    {
        var propsOriginal = "<Project>Original</Project>";
        var propsMerged = "<Project>Merged</Project>";
        var (projectPath, propsFilePath, _) = await CreateRollbackScenarioAsync(
            propsFileExisted: true,
            includePropsBackup: true,
            propsFileContent: propsMerged,
            propsBackupContent: propsOriginal);

        var service = new MigrationService(_console, backupManager: _backupManager);
        var options = new Options
        {
            Rollback = true,
            BackupDir = _testDirectory
        };

        var result = await service.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        File.ReadAllText(propsFilePath).Should().Be(propsOriginal);
        File.ReadAllText(projectPath).Should().Be("original");
    }

    private async Task<(string ProjectPath, string PropsFilePath, string BackupPath)> CreateRollbackScenarioAsync(
        bool propsFileExisted,
        bool includePropsBackup = false,
        string propsFileContent = "<Project></Project>",
        string propsBackupContent = "<Project></Project>")
    {
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);

        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        File.WriteAllText(projectPath, "modified");

        var backupFileName = "Test.csproj.backup_20240101010101000";
        File.WriteAllText(Path.Combine(backupPath, backupFileName), "original");

        var propsFilePath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsFilePath, propsFileContent);

        var manifest = new BackupManifest
        {
            Timestamp = "20240101010101000",
            PropsFilePath = propsFilePath,
            PropsFileExisted = propsFileExisted,
            Backups = new List<BackupEntry>
            {
                new()
                {
                    OriginalPath = projectPath,
                    BackupFileName = backupFileName
                }
            }
        };

        if (includePropsBackup)
        {
            var propsBackupFileName = "Directory.Packages.props.backup_20240101010101000";
            File.WriteAllText(Path.Combine(backupPath, propsBackupFileName), propsBackupContent);
            manifest.Backups.Add(new BackupEntry
            {
                OriginalPath = propsFilePath,
                BackupFileName = propsBackupFileName
            });
        }

        await _backupManager.WriteManifestAsync(backupPath, manifest);

        return (projectPath, propsFilePath, backupPath);
    }
}
