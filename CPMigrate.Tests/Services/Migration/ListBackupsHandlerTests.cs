using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

[Collection("Sequential")]
public class ListBackupsHandlerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;
    private readonly Mock<IBackupManager> _backupManagerMock;

    public ListBackupsHandlerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateListBackups_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        _backupManagerMock = new Mock<IBackupManager>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingBackupDirectory_ReturnsSuccessAndWarns()
    {
        var options = new Options { BackupDir = Path.Combine(_testDirectory, "does-not-exist") };
        var sut = new ListBackupsHandler(_backupManagerMock.Object, _console, quietMode: false);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m => m.Contains("Backup directory not found"));
        _backupManagerMock.Verify(b => b.GetBackupHistory(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NoBackupsFound_ReturnsSuccessAndInfo()
    {
        _backupManagerMock.Setup(b => b.GetBackupHistory(It.IsAny<string>())).Returns(new List<BackupSetInfo>());
        Directory.CreateDirectory(Path.Combine(_testDirectory, ".cpmigrate_backup"));
        var options = new Options { BackupDir = _testDirectory };
        var sut = new ListBackupsHandler(_backupManagerMock.Object, _console, quietMode: false);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m => m.Contains("No backups found"));
    }

    [Fact]
    public async Task ExecuteAsync_WithBackups_RendersTableAndAggregatesTotals()
    {
        var backupFile = Path.Combine(_testDirectory, "backup.dat");
        await File.WriteAllTextAsync(backupFile, new string('x', 2048));
        _backupManagerMock.Setup(b => b.GetBackupHistory(It.IsAny<string>())).Returns(new List<BackupSetInfo>
        {
            new() { Timestamp = "20240101_120000", Files = new List<string> { backupFile } },
            new() { Timestamp = "20240102_080000", Files = new List<string> { backupFile } },
        });
        Directory.CreateDirectory(Path.Combine(_testDirectory, ".cpmigrate_backup"));
        var options = new Options { BackupDir = _testDirectory };
        var sut = new ListBackupsHandler(_backupManagerMock.Object, _console, quietMode: false);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m => m.Contains("Total: 2 backup set(s), 2 file(s)"));
        _console.OutputMessages.Should().Contain(m => m.Contains("Backup directory:"));
    }

    [Fact]
    public async Task ExecuteAsync_QuietMode_SuppressesBannerAndTrailingBlankLine()
    {
        var backupFile = Path.Combine(_testDirectory, "backup.dat");
        await File.WriteAllTextAsync(backupFile, "data");
        _backupManagerMock.Setup(b => b.GetBackupHistory(It.IsAny<string>())).Returns(new List<BackupSetInfo>
        {
            new() { Timestamp = "20240101_120000", Files = new List<string> { backupFile } },
        });
        Directory.CreateDirectory(Path.Combine(_testDirectory, ".cpmigrate_backup"));
        var options = new Options { BackupDir = _testDirectory };
        var sut = new ListBackupsHandler(_backupManagerMock.Object, _console, quietMode: true);

        await sut.ExecuteAsync(options);

        _console.OutputMessages.Should().NotContain("BACKUP HISTORY");
        _console.OutputMessages.Should().Contain(m => m.Contains("Total: 1 backup set(s)"));
    }

    [Fact]
    public async Task ExecuteAsync_LegacyTimestampFallsBackToGeneralParse()
    {
        var backupFile = Path.Combine(_testDirectory, "backup.dat");
        await File.WriteAllTextAsync(backupFile, "x");
        _backupManagerMock.Setup(b => b.GetBackupHistory(It.IsAny<string>())).Returns(new List<BackupSetInfo>
        {
            new() { Timestamp = "2024-01-01T12:00:00Z", Files = new List<string> { backupFile } },
        });
        Directory.CreateDirectory(Path.Combine(_testDirectory, ".cpmigrate_backup"));
        var options = new Options { BackupDir = _testDirectory };
        var sut = new ListBackupsHandler(_backupManagerMock.Object, _console, quietMode: true);

        var result = await sut.ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m => m.Contains("Total: 1 backup set(s)"));
    }

    [Fact]
    public async Task ExecuteAsync_UnparseableTimestampIsPreservedAsRawString()
    {
        var backupFile = Path.Combine(_testDirectory, "backup.dat");
        await File.WriteAllTextAsync(backupFile, "x");
        const string raw = "not-a-timestamp-at-all";
        _backupManagerMock.Setup(b => b.GetBackupHistory(It.IsAny<string>())).Returns(new List<BackupSetInfo>
        {
            new() { Timestamp = raw, Files = new List<string> { backupFile } },
        });
        Directory.CreateDirectory(Path.Combine(_testDirectory, ".cpmigrate_backup"));
        var options = new Options { BackupDir = _testDirectory };
        var sut = new ListBackupsHandler(_backupManagerMock.Object, _console, quietMode: true);

        await sut.ExecuteAsync(options);

        _console.OutputMessages.Should().Contain(m => m.Contains("Total: 1 backup set(s)"));
    }
}