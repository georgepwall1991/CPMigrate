using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Migration;

[Collection("Sequential")]
public class RollbackHandlerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;

    public RollbackHandlerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateRollbackHandler_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
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
    public async Task ExecuteAsync_MissingBackupDirectory_ReturnsFileOperationError()
    {
        var options = new Options
        {
            Rollback = true,
            SolutionFileDir = _testDirectory,
            BackupDir = Path.Combine(_testDirectory, "absent"),
        };
        var sut = new RollbackHandler(_console, quietMode: false);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.FileOperationError);
        _console.ErrorMessages.Should().Contain(m => m.Contains("No backup directory found at"));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyManifestBackups_ReturnsSuccessNothingToRestore()
    {
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);
        await BackupManager.WriteManifestAsync(backupPath, new BackupManifest
        {
            Timestamp = "20240101010101000",
            PropsFilePath = string.Empty,
            PropsFileExisted = false,
            Backups = new List<BackupEntry>(),
        });

        var options = new Options { Rollback = true, SolutionFileDir = _testDirectory, BackupDir = _testDirectory };
        var sut = new RollbackHandler(_console, quietMode: false);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m => m.Contains("nothing to restore"));
    }

    [Fact]
    public async Task ExecuteAsync_QuietJsonMode_AutoCancelsAndReturnsSuccess()
    {
        await WriteManifestWithOneBackupAsync(propsFileExisted: false);
        var options = new Options
        {
            Rollback = true,
            SolutionFileDir = _testDirectory,
            BackupDir = _testDirectory,
            Output = OutputFormat.Json,
        };
        var sut = new RollbackHandler(_console, quietMode: true);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m => m.Contains("Rollback cancelled"));
    }

    [Fact]
    public async Task ExecuteAsync_QuietNonJsonMode_AutoProceedsWithoutPrompt()
    {
        var (projectPath, _, _) = await WriteManifestWithOneBackupAsync(propsFileExisted: false);
        File.WriteAllText(projectPath, "modified");
        var options = new Options
        {
            Rollback = true,
            SolutionFileDir = _testDirectory,
            BackupDir = _testDirectory,
        };
        var sut = new RollbackHandler(_console, quietMode: true);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        File.ReadAllText(projectPath).Should().Be("original");
        _console.OutputMessages.Should().NotContain(m => m.Contains("Proceed with rollback"));
    }

    [Fact]
    public async Task ExecuteAsync_ForceFlag_BypassesConfirmation()
    {
        var (projectPath, propsFilePath, _) = await WriteManifestWithOneBackupAsync(propsFileExisted: false);
        File.WriteAllText(projectPath, "modified");
        _console.ConfirmationResponse = false;
        var options = new Options
        {
            Rollback = true,
            Force = true,
            SolutionFileDir = _testDirectory,
            BackupDir = _testDirectory,
        };
        var sut = new RollbackHandler(_console, quietMode: false);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        File.ReadAllText(projectPath).Should().Be("original");
        File.Exists(propsFilePath).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ConfirmationDeclined_CancelledBeforeRestore()
    {
        var (projectPath, _, backupPath) = await WriteManifestWithOneBackupAsync(propsFileExisted: true);
        _console.ConfirmationResponse = false;
        var options = new Options { Rollback = true, SolutionFileDir = _testDirectory, BackupDir = _testDirectory };
        var sut = new RollbackHandler(_console, quietMode: false);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m => m.Contains("Rollback cancelled"));
        File.Exists(Path.Combine(backupPath, "Test.csproj.backup_20240101010101000")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_RestoreFailure_ReturnsFileOperationErrorAndKeepsBackup()
    {
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);
        var propsFilePath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        const string backupFileName = "Test.csproj.backup_20240101010101000";
        await BackupManager.WriteManifestAsync(backupPath, new BackupManifest
        {
            Timestamp = "20240101010101000",
            PropsFilePath = propsFilePath,
            PropsFileExisted = false,
            Backups = new List<BackupEntry>
            {
                new() { OriginalPath = projectPath, BackupFileName = backupFileName },
            },
        });

        var options = new Options { Rollback = true, SolutionFileDir = _testDirectory, BackupDir = _testDirectory };
        var sut = new RollbackHandler(_console, quietMode: true);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.FileOperationError);
        _console.ErrorMessages.Should().Contain(m => m.Contains("Failed to restore"));
        _console.OutputMessages.Should().Contain(m => m.Contains("Backup files retained for manual recovery"));
    }

    private async Task<(string ProjectPath, string PropsFilePath, string BackupPath)> WriteManifestWithOneBackupAsync(bool propsFileExisted)
    {
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);

        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        File.WriteAllText(projectPath, "modified");
        const string backupFileName = "Test.csproj.backup_20240101010101000";
        await File.WriteAllTextAsync(Path.Combine(backupPath, backupFileName), "original");

        var propsFilePath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsFilePath, "<Project></Project>");

        await BackupManager.WriteManifestAsync(backupPath, new BackupManifest
        {
            Timestamp = "20240101010101000",
            PropsFilePath = propsFilePath,
            PropsFileExisted = propsFileExisted,
            Backups = new List<BackupEntry>
            {
                new() { OriginalPath = projectPath, BackupFileName = backupFileName },
            },
        });

        return (projectPath, propsFilePath, backupPath);
    }
}