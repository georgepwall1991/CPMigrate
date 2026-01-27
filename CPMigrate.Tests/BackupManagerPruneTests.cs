using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests;

public class BackupManagerPruneTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly BackupManager _backupManager;

    public BackupManagerPruneTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateBackupPruneTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _backupManager = new BackupManager();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void PruneBackups_MoreThanRetention_DeletesOldest()
    {
        // Arrange
        // Create 3 backup sets
        CreateBackupSet("20240101_120000", "file1.csproj");
        CreateBackupSet("20240102_120000", "file2.csproj");
        CreateBackupSet("20240103_120000", "file3.csproj");

        // Keep 2
        // Act
        var result = _backupManager.PruneBackups(_testDirectory, 2);

        // Assert
        result.BackupsRemoved.Should().Be(1);
        result.KeptCount.Should().Be(2);
        
        // Oldest should be gone (20240101)
        File.Exists(Path.Combine(_testDirectory, "file1.csproj.backup_20240101_120000")).Should().BeFalse();
        // Newer ones should remain
        Directory.GetFiles(_testDirectory, "*.backup_*").Should().HaveCount(2);
    }

    [Fact]
    public void PruneBackups_LessThanRetention_KeepsAll()
    {
        // Arrange
        CreateBackupSet("20240101_120000", "file1.csproj");
        
        // Act
        var result = _backupManager.PruneBackups(_testDirectory, 5);

        // Assert
        result.BackupsRemoved.Should().Be(0);
        result.KeptCount.Should().Be(1);
        Directory.GetFiles(_testDirectory, "*.backup_*").Should().HaveCount(1);
    }

    [Fact]
    public void PruneAllBackups_DeletesEverything()
    {
        // Arrange
        CreateBackupSet("20240101_120000", "file1.csproj");
        CreateBackupSet("20240102_120000", "file2.csproj");
        
        // Also create a manifest
        var manifestPath = Path.Combine(_testDirectory, "backup_manifest.json");
        File.WriteAllText(manifestPath, "{}");

        // Act
        var result = _backupManager.PruneAllBackups(_testDirectory);

        // Assert
        result.BackupsRemoved.Should().Be(2);
        Directory.Exists(_testDirectory).Should().BeFalse(); // Parent dir should be deleted if empty
    }

    [Fact]
    public void ApplyRetention_RetentionDisabled_ReturnsEmptyResult()
    {
        // Arrange & Act
        var result = _backupManager.ApplyRetention(_testDirectory, 0);

        // Assert
        result.KeptCount.Should().Be(0);
        result.BackupsRemoved.Should().Be(0);
    }

    private void CreateBackupSet(string timestamp, string originalFileName)
    {
        var backupFileName = $"{originalFileName}.backup_{timestamp}";
        var backupPath = Path.Combine(_testDirectory, backupFileName);
        File.WriteAllText(backupPath, "dummy content");
    }
}
