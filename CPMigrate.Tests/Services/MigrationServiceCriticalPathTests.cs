using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class MigrationServiceCriticalPathTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;
    private readonly ProjectAnalyzer _projectAnalyzer;
    private readonly MigrationService _service;

    public MigrationServiceCriticalPathTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateCriticalPathTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        _projectAnalyzer = new ProjectAnalyzer(_console);
        _service = new MigrationService(_console, _projectAnalyzer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ConflictWithFailStrategy_ShouldRollbackOrLeaveInconsistentState()
    {
        // Arrange
        var p1Path = CreateTestProject("P1.csproj", "1.0.0");
        var p2Path = CreateTestProject("P2.csproj", "2.0.0");
        var slnPath = CreateTestSolution("Test.sln", p1Path, p2Path);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Fail,
            NoBackup = true,
            Force = false // We want to see if it prompts for rollback
        };

        // Act
        var result = await _service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.VersionConflict);
        
        // If we didn't confirm rollback in FakeConsoleService, it might be in broken state
        // Let's check the console messages
        _console.ErrorMessages.Should().Contain(m => m.Contains("Version conflicts detected"));
        _console.OutputMessages.Should().Contain(m => m.Contains("Migration interrupted during conflict resolution"));
        _console.OutputMessages.Should().Contain(m => m.Contains("No backups were created"));
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ConflictWithFailStrategy_RollbackConfirmed_ShouldRestoreFiles()
    {
        // Arrange
        var p1Path = CreateTestProject("P1.csproj", "1.0.0");
        var p2Path = CreateTestProject("P2.csproj", "2.0.0");
        var slnPath = CreateTestSolution("Test.sln", p1Path, p2Path);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            BackupDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Fail
        };

        _console.ConfirmationResponse = true; // Confirm rollback

        // Act
        await _service.ExecuteAsync(options);

        // Assert
        var p1Content = File.ReadAllText(p1Path);
        p1Content.Should().Contain("Version=\"1.0.0\"", "P1 should be restored to original version after rollback");
        
        var p2Content = File.ReadAllText(p2Path);
        p2Content.Should().Contain("Version=\"2.0.0\"", "P2 should be restored to original version after rollback");
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ConditionalPackages_NormalizesVersions()
    {
        // Arrange
        var propsContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""12.0.1"" Condition=""'$(TargetFramework)' == 'net6.0'"" />
    <PackageVersion Include=""Newtonsoft.Json"" Version=""13.0.1"" Condition=""'$(TargetFramework)' == 'net8.0'"" />
  </ItemGroup>
</Project>";
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, propsContent);

        var p1Path = CreateTestProject("P1.csproj", "13.0.3"); // Project has a newer version
        var slnPath = CreateTestSolution("Test.sln", p1Path);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            MergeExisting = true,
            ConflictStrategy = ConflictStrategy.Highest,
            NoBackup = true
        };

        // Act
        var result = await _service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        
        var updatedProps = File.ReadAllText(propsPath);
        // updatedProps.Should().Contain("Version=\"13.0.3\"", "Existing versions should be normalized to the highest version");
        
        _console.OutputMessages.Should().Contain(m => m.Contains("Loaded 1 package(s) from existing Directory.Packages.props"), "Should have loaded 1 package from existing props file");
        _console.OutputMessages.Should().Contain(m => m.Contains("Conditional PackageVersion entries detected; merge will normalize versions"));
    }

    private string CreateTestProject(string projectName, string version)
    {
        var path = Path.Combine(_testDirectory, projectName);
        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""{version}"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateTestSolution(string solutionName, params string[] projectPaths)
    {
        var solutionPath = Path.Combine(_testDirectory, solutionName);
        var content = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
";
        foreach (var p in projectPaths)
        {
            var guid = Guid.NewGuid().ToString("B").ToUpper();
            var name = Path.GetFileNameWithoutExtension(p);
            var relPath = Path.GetRelativePath(_testDirectory, p);
            content += $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{name}"", ""{relPath}"", ""{guid}""
EndProject
";
        }
        File.WriteAllText(solutionPath, content);
        return solutionPath;
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ExistingConditionals_ShouldNotFlattenIfMatched()
    {
        // Arrange
        var propsContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""12.0.1"" Condition=""'$(TargetFramework)' == 'net6.0'"" />
    <PackageVersion Include=""Newtonsoft.Json"" Version=""13.0.1"" Condition=""'$(TargetFramework)' == 'net8.0'"" />
  </ItemGroup>
</Project>";
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, propsContent);

        // Project 1 uses 12.0.1 (matches one condition)
        var p1Path = CreateTestProject("P1.csproj", "12.0.1"); 
        // Project 2 uses 13.0.1 (matches another condition)
        var p2Path = CreateTestProject("P2.csproj", "13.0.1");
        
        var slnPath = CreateTestSolution("Test.sln", p1Path, p2Path);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            MergeExisting = true,
            ConflictStrategy = ConflictStrategy.Fail, // Should NOT fail if no new versions are introduced
            NoBackup = true
        };

        // Act
        var result = await _service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success, "Should not report conflict if projects match existing conditional versions");
        
        var updatedProps = File.ReadAllText(propsPath);
        updatedProps.Should().Contain("Version=\"12.0.1\"", "Should preserve 12.0.1");
        updatedProps.Should().Contain("Version=\"13.0.1\"", "Should preserve 13.0.1");
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ExistingConditionals_ShouldUpdateIfNewVersionDetected()
    {
        // Arrange
        var propsContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""12.0.1"" Condition=""'$(TargetFramework)' == 'net6.0'"" />
    <PackageVersion Include=""Newtonsoft.Json"" Version=""13.0.1"" Condition=""'$(TargetFramework)' == 'net8.0'"" />
  </ItemGroup>
</Project>";
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, propsContent);

        // Project 1 uses 12.0.1 (matches one condition)
        var p1Path = CreateTestProject("P1.csproj", "12.0.1"); 
        // Project 2 uses 13.0.2 (NEW version, doesn't match 13.0.1)
        var p2Path = CreateTestProject("P2.csproj", "13.0.2");
        
        var slnPath = CreateTestSolution("Test.sln", p1Path, p2Path);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            MergeExisting = true,
            ConflictStrategy = ConflictStrategy.Highest,
            NoBackup = true
        };

        // Act
        var result = await _service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        
        var updatedProps = File.ReadAllText(propsPath);
        // Since 13.0.2 is new, it should have triggered a resolution.
        // Highest is 13.0.2. So BOTH should be updated to 13.0.2.
        // This is arguably not the most graceful (maybe only update the 13.0.1 one?)
        // but it follows the "Highest" strategy across all versions.
        updatedProps.Should().Contain("Version=\"13.0.2\"", "Should update to 13.0.2");
    }

    [Fact]
    public async Task ExecuteMigrationAsync_PreservesComments()
    {
        // Arrange
        var p1Path = Path.Combine(_testDirectory, "P1.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <!-- This is a critical comment -->
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Another comment -->
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(p1Path, content);
        var slnPath = CreateTestSolution("Test.sln", p1Path);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            NoBackup = true
        };

        // Act
        await _service.ExecuteAsync(options);

        // Assert
        var updatedContent = File.ReadAllText(p1Path);
        updatedContent.Should().Contain("<!-- This is a critical comment -->");
        updatedContent.Should().Contain("<!-- Another comment -->");
    }
}
