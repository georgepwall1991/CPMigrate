using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for MigrationService.ExecuteMigrationAsync covering critical paths,
/// conflict resolution, error handling, and edge cases.
/// </summary>
public class MigrationServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;

    public MigrationServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateMigration_{Guid.NewGuid():N}");
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
    public async Task ExecuteMigrationAsync_ValidSolution_SuccessfullyMigrates()
    {
        // Arrange
        var projectPath = CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1")
        });

        var service = new MigrationService(_console);
        var options = new Options
        {
            ProjectFileDir = projectPath,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.ProjectsProcessed.Should().Be(1);
        result.PackagesCentralized.Should().Be(1);
        result.ConflictsResolved.Should().Be(0);

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.Exists(propsPath).Should().BeTrue();
        var propsContent = File.ReadAllText(propsPath);
        propsContent.Should().Contain("Newtonsoft.Json");
        propsContent.Should().Contain("13.0.1");
    }

    [Fact]
    public async Task ExecuteMigrationAsync_WithConflicts_ResolvesWithHighestStrategy()
    {
        // Arrange
        CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "12.0.1")
        });
        CreateTestProjectFile("Project2.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1")
        });

        var service = new MigrationService(_console);
        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.ConflictsResolved.Should().Be(1);

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var propsContent = File.ReadAllText(propsPath);
        propsContent.Should().Contain("13.0.1");
        propsContent.Should().NotContain("12.0.1");
    }

    [Fact]
    public async Task ExecuteMigrationAsync_WithConflicts_ResolvesWithLowestStrategy()
    {
        // Arrange
        CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "12.0.1")
        });
        CreateTestProjectFile("Project2.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1")
        });

        var service = new MigrationService(_console);
        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Lowest
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.ConflictsResolved.Should().Be(1);

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var propsContent = File.ReadAllText(propsPath);
        propsContent.Should().Contain("12.0.1");
        propsContent.Should().NotContain("13.0.1");
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ConflictStrategyFail_ReturnsError()
    {
        // Arrange
        CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "12.0.1")
        });
        CreateTestProjectFile("Project2.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1")
        });

        var service = new MigrationService(_console);
        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Fail
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.VersionConflict);
    }

    [Fact]
    public async Task ExecuteMigrationAsync_AlreadyMigrated_ReturnsSuccessWithMessage()
    {
        // Arrange
        var projectPath = CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1")
        });

        // Pre-create Directory.Packages.props
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project></Project>");

        var service = new MigrationService(_console);
        var options = new Options
        {
            ProjectFileDir = projectPath,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PropsFilePath.Should().Be(propsPath);
    }

    [Fact]
    public async Task ExecuteMigrationAsync_NoProjectsFound_ReturnsError()
    {
        // Arrange - directory with no project files
        var service = new MigrationService(_console);
        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.NoProjectsFound);
    }

    [Fact]
    public async Task ExecuteMigrationAsync_MergeExisting_UpdatesPropsFile()
    {
        // Arrange
        var projectPath = CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1")
        });

        // Pre-create Directory.Packages.props with existing package
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var existingPropsContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(propsPath, existingPropsContent);

        var service = new MigrationService(_console);
        var options = new Options
        {
            ProjectFileDir = projectPath,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory,
            MergeExisting = true
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PackagesCentralized.Should().Be(2); // Both Serilog and Newtonsoft.Json

        var propsContent = File.ReadAllText(propsPath);
        propsContent.Should().Contain("Serilog");
        propsContent.Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public async Task ExecuteMigrationAsync_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        var projectPath = CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1")
        });
        var originalProjectContent = File.ReadAllText(projectPath);

        var service = new MigrationService(_console);
        var options = new Options
        {
            ProjectFileDir = projectPath,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory,
            DryRun = true
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.WasDryRun.Should().BeTrue();

        // Project file should be unchanged
        File.ReadAllText(projectPath).Should().Be(originalProjectContent);

        // Directory.Packages.props should not be created
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.Exists(propsPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteMigrationAsync_MultiplePackages_AllCentralized()
    {
        // Arrange
        var projectPath = CreateTestProjectFile("Project1.csproj", new[]
        {
            ("Newtonsoft.Json", "13.0.1"),
            ("Serilog", "3.0.0"),
            ("FluentAssertions", "6.11.0")
        });

        var service = new MigrationService(_console);
        var options = new Options
        {
            ProjectFileDir = projectPath,
            OutputDir = _testDirectory,
            BackupDir = _testDirectory
        };

        // Act
        var result = await service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PackagesCentralized.Should().Be(3);

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var propsContent = File.ReadAllText(propsPath);
        propsContent.Should().Contain("Newtonsoft.Json");
        propsContent.Should().Contain("Serilog");
        propsContent.Should().Contain("FluentAssertions");
    }

    // Helper methods

    private string CreateTestProjectFile(string projectName, (string PackageName, string Version)[] packages)
    {
        var packageReferences = string.Join(Environment.NewLine,
            packages.Select(p => $"    <PackageReference Include=\"{p.PackageName}\" Version=\"{p.Version}\" />"));

        var projectContent = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
{packageReferences}
  </ItemGroup>
</Project>";

        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, projectContent);
        return projectPath;
    }
}
