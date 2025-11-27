using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests;

public class FakeConsoleService : IConsoleService
{
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
    public string AskSelection(string title, IEnumerable<string> choices) => choices.FirstOrDefault() ?? "";
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
}