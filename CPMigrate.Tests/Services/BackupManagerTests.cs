using CPMigrate.Models;
using FluentAssertions;
using Xunit;
using Options = CPMigrate.Options;

namespace CPMigrate.Tests.Services;

public class BackupManagerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly BackupManager _backupManager;

    public BackupManagerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateTests_{Guid.NewGuid():N}");
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
    public void CreateBackupDirectory_NoBackupTrue_ReturnsEmptyString()
    {
        var options = new Options { NoBackup = true };

        var result = _backupManager.CreateBackupDirectory(options);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CreateBackupDirectory_NoBackupFalse_CreatesDirectory()
    {
        var options = new Options
        {
            NoBackup = false,
            BackupDir = _testDirectory
        };

        var result = _backupManager.CreateBackupDirectory(options);

        result.Should().NotBeEmpty();
        result.Should().EndWith(".cpmigrate_backup");
        Directory.Exists(result).Should().BeTrue();
    }

    [Fact]
    public void CreateBackupDirectory_EmptyBackupDir_UsesCurrentDirectory()
    {
        var options = new Options
        {
            NoBackup = false,
            BackupDir = ""
        };

        var result = _backupManager.CreateBackupDirectory(options);

        result.Should().NotBeEmpty();
        result.Should().EndWith(".cpmigrate_backup");
    }

    [Fact]
    public void CreateBackupForProject_NoBackupTrue_DoesNotCreateFile()
    {
        var projectFile = CreateTestFile("Test.csproj", "<Project></Project>");
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var options = new Options { NoBackup = true };

        _backupManager.CreateBackupForProject(options, projectFile, backupDir);

        var backupFiles = Directory.GetFiles(backupDir);
        backupFiles.Should().BeEmpty();
    }

    [Fact]
    public void CreateBackupForProject_NoBackupFalse_CreatesBackupFile()
    {
        var projectContent = "<Project><PropertyGroup></PropertyGroup></Project>";
        var projectFile = CreateTestFile("Test.csproj", projectContent);
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var options = new Options { NoBackup = false };

        _backupManager.CreateBackupForProject(options, projectFile, backupDir);

        var backupFiles = Directory.GetFiles(backupDir, "*.backup_*");
        backupFiles.Should().HaveCount(1);

        var backupContent = File.ReadAllText(backupFiles[0]);
        backupContent.Should().Be(projectContent);
    }

    [Fact]
    public void CreateBackupForProject_BackupFileName_ContainsTimestamp()
    {
        var projectFile = CreateTestFile("MyProject.csproj", "<Project></Project>");
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var options = new Options { NoBackup = false };

        _backupManager.CreateBackupForProject(options, projectFile, backupDir);

        var backupFiles = Directory.GetFiles(backupDir);
        backupFiles.Should().HaveCount(1);
        Path.GetFileName(backupFiles[0]).Should().StartWith("MyProject.csproj.backup_");
        Path.GetFileName(backupFiles[0]).Should().MatchRegex(@"MyProject\.csproj\.backup_\d{14}");
    }

    [Fact]
    public async Task ManageGitIgnore_AddGitignoreFalse_DoesNotCreateFile()
    {
        var options = new Options
        {
            AddBackupToGitignore = false,
            NoBackup = false,
            GitignoreDir = _testDirectory
        };
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");

        await _backupManager.ManageGitIgnore(options, backupPath);

        var gitignorePath = Path.Combine(_testDirectory, ".gitignore");
        File.Exists(gitignorePath).Should().BeFalse();
    }

    [Fact]
    public async Task ManageGitIgnore_AddGitignoreTrue_CreatesGitignoreFile()
    {
        var options = new Options
        {
            AddBackupToGitignore = true,
            NoBackup = false,
            GitignoreDir = _testDirectory
        };
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);

        await _backupManager.ManageGitIgnore(options, backupPath);

        var gitignorePath = Path.Combine(_testDirectory, ".gitignore");
        File.Exists(gitignorePath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(gitignorePath);
        content.Should().Contain(".cpmigrate_backup");
        content.Should().Contain("# CPMigrate backup directory");
    }

    [Fact]
    public async Task ManageGitIgnore_ExistingGitignore_AppendsEntry()
    {
        var existingContent = "node_modules/\nbin/\nobj/\n";
        var gitignorePath = Path.Combine(_testDirectory, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, existingContent);

        var options = new Options
        {
            AddBackupToGitignore = true,
            NoBackup = false,
            GitignoreDir = _testDirectory
        };
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);

        await _backupManager.ManageGitIgnore(options, backupPath);

        var content = await File.ReadAllTextAsync(gitignorePath);
        content.Should().Contain("node_modules/");
        content.Should().Contain(".cpmigrate_backup");
    }

    [Fact]
    public async Task ManageGitIgnore_EntryAlreadyExists_DoesNotDuplicate()
    {
        var existingContent = "node_modules/\n.cpmigrate_backup/\n";
        var gitignorePath = Path.Combine(_testDirectory, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, existingContent);

        var options = new Options
        {
            AddBackupToGitignore = true,
            NoBackup = false,
            GitignoreDir = _testDirectory
        };
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);

        await _backupManager.ManageGitIgnore(options, backupPath);

        var content = await File.ReadAllTextAsync(gitignorePath);
        var occurrences = content.Split(".cpmigrate_backup").Length - 1;
        occurrences.Should().Be(1, "backup directory should not be duplicated");
    }

    private string CreateTestFile(string fileName, string content)
    {
        var filePath = Path.Combine(_testDirectory, fileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Fact]
    public async Task WriteManifestAsync_CreatesJsonFile()
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var manifest = new BackupManifest
        {
            Timestamp = "20231127120000",
            PropsFilePath = "/path/to/Directory.Packages.props",
            Backups = new List<BackupEntry>
            {
                new() { OriginalPath = "/path/to/Project1.csproj", BackupFileName = "Project1.csproj.backup_20231127120000" },
                new() { OriginalPath = "/path/to/Project2.csproj", BackupFileName = "Project2.csproj.backup_20231127120000" }
            }
        };

        await _backupManager.WriteManifestAsync(backupDir, manifest);

        var manifestPath = Path.Combine(backupDir, "backup_manifest.json");
        File.Exists(manifestPath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(manifestPath);
        content.Should().Contain("timestamp");
        content.Should().Contain("propsFilePath");
        content.Should().Contain("backups");
    }

    [Fact]
    public async Task ReadManifestAsync_ValidManifest_ReturnsManifest()
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var manifest = new BackupManifest
        {
            Timestamp = "20231127120000",
            PropsFilePath = "/path/to/Directory.Packages.props",
            Backups = new List<BackupEntry>
            {
                new() { OriginalPath = "/path/to/Project1.csproj", BackupFileName = "Project1.csproj.backup_20231127120000" }
            }
        };

        await _backupManager.WriteManifestAsync(backupDir, manifest);

        var result = await _backupManager.ReadManifestAsync(backupDir);

        result.Should().NotBeNull();
        result!.Timestamp.Should().Be("20231127120000");
        result.PropsFilePath.Should().Be("/path/to/Directory.Packages.props");
        result.Backups.Should().HaveCount(1);
        result.Backups[0].OriginalPath.Should().Be("/path/to/Project1.csproj");
    }

    [Fact]
    public async Task ReadManifestAsync_NoManifest_ReturnsNull()
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var result = await _backupManager.ReadManifestAsync(backupDir);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadManifestAsync_InvalidJson_ReturnsNull()
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var manifestPath = Path.Combine(backupDir, "backup_manifest.json");
        await File.WriteAllTextAsync(manifestPath, "not valid json {{{");

        var result = await _backupManager.ReadManifestAsync(backupDir);

        result.Should().BeNull();
    }

    [Fact]
    public void RestoreFile_ValidBackup_RestoresFile()
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var originalContent = "<Project><PropertyGroup></PropertyGroup></Project>";
        var modifiedContent = "<Project><PropertyGroup>Modified</PropertyGroup></Project>";

        var originalPath = Path.Combine(_testDirectory, "Test.csproj");
        var backupFileName = "Test.csproj.backup_20231127120000";
        var backupPath = Path.Combine(backupDir, backupFileName);

        File.WriteAllText(originalPath, modifiedContent);
        File.WriteAllText(backupPath, originalContent);

        var entry = new BackupEntry
        {
            OriginalPath = originalPath,
            BackupFileName = backupFileName
        };

        _backupManager.RestoreFile(backupDir, entry);

        var restoredContent = File.ReadAllText(originalPath);
        restoredContent.Should().Be(originalContent);
    }

    [Fact]
    public void RestoreFile_BackupNotFound_ThrowsException()
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var entry = new BackupEntry
        {
            OriginalPath = Path.Combine(_testDirectory, "Test.csproj"),
            BackupFileName = "NonExistent.csproj.backup_20231127120000"
        };

        var action = () => _backupManager.RestoreFile(backupDir, entry);
        action.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void CleanupBackups_RemovesBackupFilesAndManifest()
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var backupFileName = "Test.csproj.backup_20231127120000";
        var backupFilePath = Path.Combine(backupDir, backupFileName);
        var manifestPath = Path.Combine(backupDir, "backup_manifest.json");

        File.WriteAllText(backupFilePath, "content");
        File.WriteAllText(manifestPath, "{}");

        var manifest = new BackupManifest
        {
            Timestamp = "20231127120000",
            PropsFilePath = "/path/to/props",
            Backups = new List<BackupEntry>
            {
                new() { OriginalPath = "/path/to/Test.csproj", BackupFileName = backupFileName }
            }
        };

        var errors = _backupManager.CleanupBackups(backupDir, manifest);

        errors.Should().BeEmpty();
        File.Exists(backupFilePath).Should().BeFalse();
        File.Exists(manifestPath).Should().BeFalse();
        Directory.Exists(backupDir).Should().BeFalse();
    }
}
