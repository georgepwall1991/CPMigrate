using System.Text.Json;
using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests;

public class FakeConsoleService : IConsoleService
{
    public bool ConfirmationResponse { get; set; } = true;
    public Queue<string> TextResponses { get; set; } = new();
    public Queue<string> SelectionResponses { get; set; } = new();

    public void Info(string message) { }
    public void Success(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
    public void Highlight(string message) { }
    public void Dim(string message) { }
    public void DryRun(string message) { }
    public void WriteHeader() { }
    public void Banner(string message) { }
    public void Separator() { }
    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions, List<string> conflicts, ConflictStrategy strategy) { }
    public void WriteSummaryTable(int projectCount, int packageCount, int conflictCount, string propsFilePath, string? backupPath, bool wasDryRun) { }
    public void WriteProjectTree(List<string> projectPaths, string basePath) { }
    public void WritePropsPreview(string content) { }
    public void WriteMarkup(string message) { }
    public void WriteLine(string message = "") { }
    public string AskSelection(string title, IEnumerable<string> choices)
    {
        if (SelectionResponses.Count > 0)
            return SelectionResponses.Dequeue();
        return choices.FirstOrDefault() ?? "";
    }
    public bool AskConfirmation(string message) => ConfirmationResponse;
    public string AskText(string prompt, string defaultValue = "")
    {
        if (TextResponses.Count > 0)
            return TextResponses.Dequeue();
        return defaultValue;
    }
    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath) { }
    public void WriteAnalysisHeader(int projectCount, int packageCount) { }
    public void WriteAnalyzerResult(AnalyzerResult result) { }
    public void WriteAnalysisSummary(AnalysisReport report) { }
}

public class OptionsTests
{
    #region Options Validation Tests

    [Fact]
    public void Validate_NoBackupWithGitignore_ThrowsArgumentException()
    {
        // Arrange
        var options = new Options
        {
            NoBackup = true,
            AddBackupToGitignore = true
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--add-gitignore cannot be used with --no-backup*");
    }

    [Fact]
    public void Validate_NoBackupWithEmptyBackupDir_ThrowsArgumentException()
    {
        // Arrange
        var options = new Options
        {
            NoBackup = false,
            BackupDir = ""
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*backup-dir must be specified*");
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        // Arrange
        var options = new Options
        {
            NoBackup = false,
            BackupDir = ".",
            AddBackupToGitignore = false
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new Options();

        // Assert
        options.DryRun.Should().BeFalse();
        options.NoBackup.Should().BeFalse();
        options.KeepAttributes.Should().BeFalse();
        options.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
    }

    #endregion
}

public class PropsGeneratorTests
{
    private readonly PropsGenerator _generator = new();

    #region Generate Tests

    [Fact]
    public void Generate_SinglePackage_GeneratesValidXml()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>> 
        {
            ["Newtonsoft.Json"] = new() { "13.0.1" }
        };

        // Act
        var result = _generator.Generate(packageVersions);

        // Assert
        result.Should().Contain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        result.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        result.Should().Contain("<Project>");
        result.Should().Contain("</Project>");
    }

    [Fact]
    public void Generate_MultiplePackages_GeneratesAllEntries()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>> 
        {
            ["Newtonsoft.Json"] = new() { "13.0.1" },
            ["Serilog"] = new() { "3.0.0" },
            ["xunit"] = new() { "2.9.0" }
        };

        // Act
        var result = _generator.Generate(packageVersions);

        // Assert
        result.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        result.Should().Contain("<PackageVersion Include=\"Serilog\" Version=\"3.0.0\" />");
        result.Should().Contain("<PackageVersion Include=\"xunit\" Version=\"2.9.0\" />");
    }

    [Fact]
    public void Generate_EmptyDictionary_GeneratesValidStructure()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>>();

        // Act
        var result = _generator.Generate(packageVersions);

        // Assert
        result.Should().Contain("<Project>");
        result.Should().Contain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        result.Should().Contain("<ItemGroup>");
        result.Should().Contain("</ItemGroup>");
        result.Should().Contain("</Project>");
    }

    [Fact]
    public void Generate_PackageWithMultipleVersions_ResolvesToHighestByDefault()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>> 
        {
            ["Newtonsoft.Json"] = new() { "12.0.0", "13.0.1" }
        };

        // Act
        var result = _generator.Generate(packageVersions, ConflictStrategy.Highest);

        // Assert - Should only contain highest version
        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("13.0.1");
        result.Should().NotContain("12.0.0");
    }

    [Fact]
    public void Generate_PackagesAreSortedAlphabetically()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>> 
        {
            ["Zulu"] = new() { "1.0.0" },
            ["Alpha"] = new() { "2.0.0" },
            ["Mango"] = new() { "3.0.0" }
        };

        // Act
        var result = _generator.Generate(packageVersions);

        // Assert - Alpha should appear before Mango, Mango before Zulu
        var alphaIndex = result.IndexOf("Alpha");
        var mangoIndex = result.IndexOf("Mango");
        var zuluIndex = result.IndexOf("Zulu");

        alphaIndex.Should().BeLessThan(mangoIndex);
        mangoIndex.Should().BeLessThan(zuluIndex);
    }

    #endregion
}

public class VersionResolverTests
{
    private readonly VersionResolver _resolver = new();

    #region Version Conflict Detection Tests

    [Fact]
    public void DetectConflicts_NoConflicts_ReturnsEmptyList()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>> 
        {
            ["PackageA"] = new() { "1.0.0" },
            ["PackageB"] = new() { "2.0.0" }
        };

        // Act
        var conflicts = _resolver.DetectConflicts(packageVersions);

        // Assert
        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void DetectConflicts_WithConflicts_ReturnsConflictingPackages()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>> 
        {
            ["PackageA"] = new() { "1.0.0", "2.0.0" },
            ["PackageB"] = new() { "3.0.0" },
            ["PackageC"] = new() { "4.0.0", "5.0.0", "6.0.0" }
        };

        // Act
        var conflicts = _resolver.DetectConflicts(packageVersions);

        // Assert
        conflicts.Should().HaveCount(2);
        conflicts.Should().Contain("PackageA");
        conflicts.Should().Contain("PackageC");
        conflicts.Should().NotContain("PackageB");
    }

    [Fact]
    public void DetectConflicts_ResultIsSortedAlphabetically()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>> 
        {
            ["Zulu"] = new() { "1.0.0", "2.0.0" },
            ["Alpha"] = new() { "1.0.0", "2.0.0" },
            ["Mango"] = new() { "1.0.0", "2.0.0" }
        };

        // Act
        var conflicts = _resolver.DetectConflicts(packageVersions);

        // Assert
        conflicts.Should().BeInAscendingOrder();
    }

    #endregion

    #region Version Resolution Tests

    [Fact]
    public void ResolveVersion_HighestStrategy_ReturnsHighestVersion()
    {
        // Arrange
        var versions = new[] { "1.0.0", "3.0.0", "2.0.0" };

        // Act
        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Highest);

        // Assert
        result.Should().Be("3.0.0");
    }

    [Fact]
    public void ResolveVersion_LowestStrategy_ReturnsLowestVersion()
    {
        // Arrange
        var versions = new[] { "1.0.0", "3.0.0", "2.0.0" };

        // Act
        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Lowest);

        // Assert
        result.Should().Be("1.0.0");
    }

    [Fact]
    public void ResolveVersion_WithPrereleaseVersions_ComparesNumericPartOnly()
    {
        // Arrange
        // Note: 2.0.0-beta is higher than 1.0.0 release because numeric part is compared
        var versions = new[] { "1.0.0", "2.0.0-beta", "1.5.0-alpha" };

        // Act
        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Highest);

        // Assert
        result.Should().Be("2.0.0-beta"); // 2.0.0 > 1.5.0 > 1.0.0
    }

    [Fact]
    public void ResolveVersion_WithMajorVersionDifference_ComparesCorrectly()
    {
        // Arrange
        var versions = new[] { "2.0.0", "10.0.0", "1.0.0" };

        // Act
        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Highest);

        // Assert
        result.Should().Be("10.0.0");
    }

    #endregion
}

public class ProjectAnalyzerTests
{
    private readonly ProjectAnalyzer _analyzer = new(new FakeConsoleService());

    #region DiscoverProjectsFromSolution Tests

    [Fact]
    public void DiscoverProjectsFromSolution_NonExistentDirectory_ReturnsEmpty()
    {
        // Arrange & Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution("/non/existent/path");

        // Assert
        projectPaths.Should().BeEmpty();
    }

    #endregion

    #region DiscoverProjectFromPath Tests

    [Fact]
    public void DiscoverProjectFromPath_NonExistentPath_ReturnsEmpty()
    {
        // Arrange & Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath("/non/existent/project.csproj");

        // Assert
        projectPaths.Should().BeEmpty();
    }

    #endregion
}

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
        // Arrange
        var options = new Options { NoBackup = true };

        // Act
        var result = _backupManager.CreateBackupDirectory(options);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void CreateBackupDirectory_NoBackupFalse_CreatesDirectory()
    {
        // Arrange
        var options = new Options
        {
            NoBackup = false,
            BackupDir = _testDirectory
        };

        // Act
        var result = _backupManager.CreateBackupDirectory(options);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().EndWith(".cpmigrate_backup");
        Directory.Exists(result).Should().BeTrue();
    }

    [Fact]
    public void CreateBackupDirectory_EmptyBackupDir_UsesCurrentDirectory()
    {
        // Arrange
        var options = new Options
        {
            NoBackup = false,
            BackupDir = ""
        };

        // Act
        var result = _backupManager.CreateBackupDirectory(options);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().EndWith(".cpmigrate_backup");
    }

    [Fact]
    public void CreateBackupForProject_NoBackupTrue_DoesNotCreateFile()
    {
        // Arrange
        var projectFile = CreateTestFile("Test.csproj", "<Project></Project>");
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var options = new Options { NoBackup = true };

        // Act
        _backupManager.CreateBackupForProject(options, projectFile, backupDir);

        // Assert
        var backupFiles = Directory.GetFiles(backupDir);
        backupFiles.Should().BeEmpty();
    }

    [Fact]
    public void CreateBackupForProject_NoBackupFalse_CreatesBackupFile()
    {
        // Arrange
        var projectContent = "<Project><PropertyGroup></PropertyGroup></Project>";
        var projectFile = CreateTestFile("Test.csproj", projectContent);
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var options = new Options { NoBackup = false };

        // Act
        _backupManager.CreateBackupForProject(options, projectFile, backupDir);

        // Assert
        var backupFiles = Directory.GetFiles(backupDir, "*.backup_*");
        backupFiles.Should().HaveCount(1);

        var backupContent = File.ReadAllText(backupFiles[0]);
        backupContent.Should().Be(projectContent);
    }

    [Fact]
    public void CreateBackupForProject_BackupFileName_ContainsTimestamp()
    {
        // Arrange
        var projectFile = CreateTestFile("MyProject.csproj", "<Project></Project>");
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var options = new Options { NoBackup = false };

        // Act
        _backupManager.CreateBackupForProject(options, projectFile, backupDir);

        // Assert
        var backupFiles = Directory.GetFiles(backupDir);
        backupFiles.Should().HaveCount(1);
        Path.GetFileName(backupFiles[0]).Should().StartWith("MyProject.csproj.backup_");
        Path.GetFileName(backupFiles[0]).Should().MatchRegex(@"MyProject\.csproj\.backup_\d{14}");
    }

    [Fact]
    public async Task ManageGitIgnore_AddGitignoreFalse_DoesNotCreateFile()
    {
        // Arrange
        var options = new Options
        {
            AddBackupToGitignore = false,
            NoBackup = false,
            GitignoreDir = _testDirectory
        };
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");

        // Act
        await _backupManager.ManageGitIgnore(options, backupPath);

        // Assert
        var gitignorePath = Path.Combine(_testDirectory, ".gitignore");
        File.Exists(gitignorePath).Should().BeFalse();
    }

    [Fact]
    public async Task ManageGitIgnore_AddGitignoreTrue_CreatesGitignoreFile()
    {
        // Arrange
        var options = new Options
        {
            AddBackupToGitignore = true,
            NoBackup = false,
            GitignoreDir = _testDirectory
        };
        var backupPath = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupPath);

        // Act
        await _backupManager.ManageGitIgnore(options, backupPath);

        // Assert
        var gitignorePath = Path.Combine(_testDirectory, ".gitignore");
        File.Exists(gitignorePath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(gitignorePath);
        content.Should().Contain(".cpmigrate_backup");
        content.Should().Contain("# CPMigrate backup directory");
    }

    [Fact]
    public async Task ManageGitIgnore_ExistingGitignore_AppendsEntry()
    {
        // Arrange
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

        // Act
        await _backupManager.ManageGitIgnore(options, backupPath);

        // Assert
        var content = await File.ReadAllTextAsync(gitignorePath);
        content.Should().Contain("node_modules/");
        content.Should().Contain(".cpmigrate_backup");
    }

    [Fact]
    public async Task ManageGitIgnore_EntryAlreadyExists_DoesNotDuplicate()
    {
        // Arrange
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

        // Act
        await _backupManager.ManageGitIgnore(options, backupPath);

        // Assert
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

    #region Manifest Tests

    [Fact]
    public async Task WriteManifestAsync_CreatesJsonFile()
    {
        // Arrange
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

        // Act
        await _backupManager.WriteManifestAsync(backupDir, manifest);

        // Assert
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
        // Arrange
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

        // Act
        var result = await _backupManager.ReadManifestAsync(backupDir);

        // Assert
        result.Should().NotBeNull();
        result!.Timestamp.Should().Be("20231127120000");
        result.PropsFilePath.Should().Be("/path/to/Directory.Packages.props");
        result.Backups.Should().HaveCount(1);
        result.Backups[0].OriginalPath.Should().Be("/path/to/Project1.csproj");
    }

    [Fact]
    public async Task ReadManifestAsync_NoManifest_ReturnsNull()
    {
        // Arrange
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        // Act
        var result = await _backupManager.ReadManifestAsync(backupDir);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadManifestAsync_InvalidJson_ReturnsNull()
    {
        // Arrange
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var manifestPath = Path.Combine(backupDir, "backup_manifest.json");
        await File.WriteAllTextAsync(manifestPath, "not valid json {{{");

        // Act
        var result = await _backupManager.ReadManifestAsync(backupDir);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Restore Tests

    [Fact]
    public void RestoreFile_ValidBackup_RestoresFile()
    {
        // Arrange
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var originalContent = "<Project><PropertyGroup></PropertyGroup></Project>";
        var modifiedContent = "<Project><PropertyGroup>Modified</PropertyGroup></Project>";

        var originalPath = Path.Combine(_testDirectory, "Test.csproj");
        var backupFileName = "Test.csproj.backup_20231127120000";
        var backupPath = Path.Combine(backupDir, backupFileName);

        // Create the "modified" current file
        File.WriteAllText(originalPath, modifiedContent);
        // Create the backup with original content
        File.WriteAllText(backupPath, originalContent);

        var entry = new BackupEntry
        {
            OriginalPath = originalPath,
            BackupFileName = backupFileName
        };

        // Act
        _backupManager.RestoreFile(backupDir, entry);

        // Assert
        var restoredContent = File.ReadAllText(originalPath);
        restoredContent.Should().Be(originalContent);
    }

    [Fact]
    public void RestoreFile_BackupNotFound_ThrowsException()
    {
        // Arrange
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var entry = new BackupEntry
        {
            OriginalPath = Path.Combine(_testDirectory, "Test.csproj"),
            BackupFileName = "NonExistent.csproj.backup_20231127120000"
        };

        // Act & Assert
        var action = () => _backupManager.RestoreFile(backupDir, entry);
        action.Should().Throw<FileNotFoundException>();
    }

    #endregion

    #region Cleanup Tests

    [Fact]
    public void CleanupBackups_RemovesBackupFilesAndManifest()
    {
        // Arrange
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

        // Act
        _backupManager.CleanupBackups(backupDir, manifest);

        // Assert
        File.Exists(backupFilePath).Should().BeFalse();
        File.Exists(manifestPath).Should().BeFalse();
        Directory.Exists(backupDir).Should().BeFalse(); // Empty directory should be deleted
    }

    #endregion
}

public class RollbackOptionsTests
{
    [Fact]
    public void Validate_RollbackWithDryRun_ThrowsArgumentException()
    {
        // Arrange
        var options = new Options
        {
            Rollback = true,
            DryRun = true,
            BackupDir = "."
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--rollback cannot be used with --dry-run*");
    }

    [Fact]
    public void Validate_RollbackWithEmptyBackupDir_ThrowsArgumentException()
    {
        // Arrange
        var options = new Options
        {
            Rollback = true,
            BackupDir = ""
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*backup-dir must be specified for rollback*");
    }

    [Fact]
    public void Validate_RollbackWithValidBackupDir_DoesNotThrow()
    {
        // Arrange
        var options = new Options
        {
            Rollback = true,
            BackupDir = "."
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Options_RollbackDefault_IsFalse()
    {
        // Arrange & Act
        var options = new Options();

        // Assert
        options.Rollback.Should().BeFalse();
    }
}

public class AnalyzeOptionsTests
{
    [Fact]
    public void Validate_AnalyzeWithDryRun_ThrowsArgumentException()
    {
        // Arrange
        var options = new Options
        {
            Analyze = true,
            DryRun = true
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--analyze cannot be used with --dry-run*");
    }

    [Fact]
    public void Validate_AnalyzeWithRollback_ThrowsArgumentException()
    {
        // Arrange
        var options = new Options
        {
            Analyze = true,
            Rollback = true,
            BackupDir = "."
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--analyze cannot be used with --rollback*");
    }

    [Fact]
    public void Validate_AnalyzeOnly_DoesNotThrow()
    {
        // Arrange
        var options = new Options
        {
            Analyze = true
        };

        // Act & Assert
        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Options_AnalyzeDefault_IsFalse()
    {
        // Arrange & Act
        var options = new Options();

        // Assert
        options.Analyze.Should().BeFalse();
    }
}

public class VersionInconsistencyAnalyzerTests
{
    private readonly VersionInconsistencyAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_PackagesWithSameVersion_ReturnsNoIssues()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeFalse();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_PackagesWithDifferentVersions_ReturnsIssues()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "12.0.3", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Newtonsoft.Json");
    }

    [Fact]
    public void Analyze_GroupsPackageNamesCaseInsensitively()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("newtonsoft.json", "12.0.3", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
    }
}

public class DuplicatePackageAnalyzerTests
{
    private readonly DuplicatePackageAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_NoDuplicates_ReturnsNoIssues()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Serilog", "3.0.0", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeFalse();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_DifferentCasing_ReturnsIssues()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("newtonsoft.json", "13.0.1", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("casing variations");
    }

    [Fact]
    public void Analyze_ReportsAllCasingVariations()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("newtonsoft.json", "13.0.1", "/path/Project2.csproj", "Project2.csproj"),
            new("NEWTONSOFT.JSON", "13.0.1", "/path/Project3.csproj", "Project3.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("3 casing variations");
    }
}

public class RedundantReferenceAnalyzerTests
{
    private readonly RedundantReferenceAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_NoRedundantReferences_ReturnsNoIssues()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Serilog", "3.0.0", "/path/Project1.csproj", "Project1.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeFalse();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_RedundantReferencesInSameProject_ReturnsIssues()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Newtonsoft.Json");
        result.Issues[0].AffectedProjects.Should().Contain("Project1.csproj");
    }

    [Fact]
    public void Analyze_SamePackageDifferentProjects_ReturnsNoIssues()
    {
        // Arrange - same package in different projects is fine
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeFalse();
    }
}

public class AnalysisServiceTests
{
    [Fact]
    public void Analyze_RunsAllAnalyzers()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.Results.Should().HaveCount(3); // Three analyzers
        report.ProjectsScanned.Should().Be(1);
        report.TotalPackageReferences.Should().Be(1);
    }

    [Fact]
    public void Analyze_NoIssues_HasIssuesIsFalse()
    {
        // Arrange
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.HasIssues.Should().BeFalse();
        report.TotalIssues.Should().Be(0);
    }

    [Fact]
    public void Analyze_WithIssues_HasIssuesIsTrue()
    {
        // Arrange - version inconsistency
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "12.0.3", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.HasIssues.Should().BeTrue();
        report.TotalIssues.Should().BeGreaterThan(0);
    }
}