using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for BuildPropsService covering property unification, 60% threshold logic,
/// Directory.Build.props creation/update, and property/item removal from projects.
/// </summary>
public class BuildPropsServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;
    private readonly ProjectAnalyzer _projectAnalyzer;
    private readonly BuildPropsService _service;

    public BuildPropsServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateBuildPropsServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        _projectAnalyzer = new ProjectAnalyzer(_console);
        _service = new BuildPropsService(_console, _projectAnalyzer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UnifyPropertiesAsync_NoProjectsFound_ReturnsError()
    {
        // Arrange - Empty directory with no solution
        var options = new Options
        {
            SolutionFileDir = _testDirectory
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.UnexpectedError);
    }

    [Fact]
    public async Task UnifyPropertiesAsync_NoCommonProperties_ReturnsSuccessWithMessage()
    {
        // Arrange - Projects with different properties (no 60% consensus)
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net7.0</TargetFramework>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
  </PropertyGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true // Skip confirmation
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);
        // Should have message about no common properties
    }

    [Fact]
    public async Task UnifyPropertiesAsync_60PercentConsensus_UnifiesProperties()
    {
        // Arrange - 3 projects with same Nullable property (100% consensus)
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Directory.Build.props should exist
        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        File.Exists(buildPropsPath).Should().BeTrue();

        // Should contain TargetFramework and Nullable
        var content = File.ReadAllText(buildPropsPath);
        content.Should().Contain("<TargetFramework>net8.0</TargetFramework>");
        content.Should().Contain("<Nullable>enable</Nullable>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ItemsAboveThreshold_UnifiesItems()
    {
        // Arrange - 2 projects with same Using item (100% consensus)
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Directory.Build.props should contain Using item
        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        var content = File.ReadAllText(buildPropsPath);
        content.Should().Contain("<Using Include=\"System.Text.Json\"");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            DryRun = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Directory.Build.props should NOT be created
        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        File.Exists(buildPropsPath).Should().BeFalse();
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ForceMode_SkipsConfirmation()
    {
        // Arrange - Set confirmation to false, but Force=true should bypass
        _console.ConfirmationResponse = false;

        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Should still create file despite ConfirmationResponse = false
        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        File.Exists(buildPropsPath).Should().BeTrue();
    }

    [Fact]
    public async Task UnifyPropertiesAsync_UserDeclinesConfirmation_ReturnsSuccessWithoutChanges()
    {
        // Arrange
        _console.ConfirmationResponse = false;

        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = false // Require confirmation
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Directory.Build.props should NOT be created
        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        File.Exists(buildPropsPath).Should().BeFalse();
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ExistingBuildProps_UpdatesFile()
    {
        // Arrange - Create existing Directory.Build.props with one property
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>")
        );

        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        File.WriteAllText(buildPropsPath, @"<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>");

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Should have both old and new properties
        var content = File.ReadAllText(buildPropsPath);
        content.Should().Contain("<LangVersion>latest</LangVersion>"); // Existing
        content.Should().Contain("<Nullable>enable</Nullable>"); // New
        content.Should().Contain("<ImplicitUsings>enable</ImplicitUsings>"); // New
    }

    [Fact]
    public async Task UnifyPropertiesAsync_PropertiesRemovedFromProjects()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var project2Path = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var solutionPath = CreateTestSolution("TestSolution.sln", project1Path, project2Path);

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Nullable should be removed from both projects
        var project1Content = File.ReadAllText(project1Path);
        var project2Content = File.ReadAllText(project2Path);

        project1Content.Should().NotContain("<Nullable>enable</Nullable>");
        project2Content.Should().NotContain("<Nullable>enable</Nullable>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ItemsRemovedFromProjects()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
</Project>");

        var project2Path = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
</Project>");

        var solutionPath = CreateTestSolution("TestSolution.sln", project1Path, project2Path);

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Using item should be removed from both projects
        var project1Content = File.ReadAllText(project1Path);
        var project2Content = File.ReadAllText(project2Path);

        project1Content.Should().NotContain("<Using Include=\"System.Text.Json\"");
        project2Content.Should().NotContain("<Using Include=\"System.Text.Json\"");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ValueMismatch_SkipsPropertyRemoval()
    {
        // Arrange - Projects with different property values to test value mismatch logic
        // Since no 60% consensus, nothing will be unified, but we can test defensive logic
        var project1Path = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var project2Path = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var project3Path = CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>");

        var solutionPath = CreateTestSolution("TestSolution.sln", project1Path, project2Path, project3Path);

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // With 2/3 having "enable", it meets 60% threshold, so will be unified
        // Project1 and Project2 should have Nullable removed (match "enable")
        var project1Content = File.ReadAllText(project1Path);
        project1Content.Should().NotContain("<Nullable>enable</Nullable>");

        var project2Content = File.ReadAllText(project2Path);
        project2Content.Should().NotContain("<Nullable>enable</Nullable>");

        // Project3 has different value (disable), so it keeps its property
        var project3Content = File.ReadAllText(project3Path);
        project3Content.Should().Contain("<Nullable>disable</Nullable>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_EmptyPropertyGroupsRemoved()
    {
        // Arrange - Project with only one property that will be unified
        var project1Path = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var project2Path = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var solutionPath = CreateTestSolution("TestSolution.sln", project1Path, project2Path);

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // PropertyGroup should be removed when empty
        var project1Content = File.ReadAllText(project1Path);
        project1Content.Should().NotContain("<PropertyGroup>");
        project1Content.Should().NotContain("</PropertyGroup>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_EmptyItemGroupsRemoved()
    {
        // Arrange - Project with only one item that will be unified
        var project1Path = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
</Project>");

        var project2Path = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
</Project>");

        var solutionPath = CreateTestSolution("TestSolution.sln", project1Path, project2Path);

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // ItemGroup containing Using should be removed when empty
        var project1Content = File.ReadAllText(project1Path);

        // Verify ItemGroup tags are gone (or at least the Using item is gone)
        project1Content.Should().NotContain("<Using Include=\"System.Text.Json\"");

        // Empty ItemGroups should be cleaned up
        // If there are ItemGroup tags, they should not be empty
        if (project1Content.Contains("<ItemGroup>"))
        {
            // If ItemGroup exists, it must have items in it
            using var collection = new ProjectCollection();
            var root = ProjectRootElement.Open(project1Path, collection);
            foreach (var itemGroup in root.ItemGroups)
            {
                itemGroup.Count.Should().BeGreaterThan(0, "empty ItemGroups should have been removed");
            }
        }
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ExactlyAtThreshold_Unifies()
    {
        // Arrange - 5 projects, 3 with same property (60%)
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project4.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project5.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Nullable=enable should be unified (3/5 = 60%)
        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        var content = File.ReadAllText(buildPropsPath);
        content.Should().Contain("<Nullable>enable</Nullable>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_JustBelowThreshold_DoesNotUnify()
    {
        // Arrange - 5 projects, 2 with same property (40%, below 60%)
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project4.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project5.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>")
        );

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // No Nullable should be unified (2/5 = 40% < 60%)
        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        File.Exists(buildPropsPath).Should().BeFalse();
    }

    // ── Backups — a unify run rewrites every consensus project; it owes the same undo path ───

    [Fact]
    public async Task UnifyPropertiesAsync_BacksUpEveryFileItOverwrites()
    {
        var project1Path = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");
        var project2Path = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");
        var solutionPath = CreateTestSolution("TestSolution.sln", project1Path, project2Path);
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var project1Original = await File.ReadAllTextAsync(project1Path);

        var result = await _service.UnifyPropertiesAsync(new Options
        {
            SolutionFileDir = solutionDir,
            Force = true,
        });

        result.Should().Be(ExitCodes.Success);

        var backupDir = Path.Combine(solutionDir, ".cpmigrate_backup");
        Directory.Exists(backupDir).Should().BeTrue("every overwritten file is recoverable");

        var manifest = await BackupManager.ReadManifestAsync(backupDir);
        manifest.Should().NotBeNull();
        manifest!.Backups.Should().Contain(b => b.OriginalPath == project1Path);
        manifest.Backups.Should().Contain(b => b.OriginalPath == project2Path);
        manifest.PropsFileExisted.Should().BeFalse(
            "the Directory.Build.props the run created is removed again by --rollback"
        );

        // The manifest is the one --rollback reads — prove the round trip, not just the file list.
        var rollback = new CPMigrate.Services.Migration.RollbackHandler(_console, quietMode: true);
        var rollbackResult = await rollback.ExecuteAsync(new Options
        {
            Rollback = true,
            BackupDir = solutionDir,
            Force = true,
        });

        rollbackResult.ExitCode.Should().Be(ExitCodes.Success);
        (await File.ReadAllTextAsync(project1Path)).Should().Be(project1Original);
        File.Exists(Path.Combine(solutionDir, "Directory.Build.props"))
            .Should().BeFalse("a file the run created is removed by rollback");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_DryRun_LeavesNoBackupBehind()
    {
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"));

        var result = await _service.UnifyPropertiesAsync(new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            DryRun = true,
        });

        result.Should().Be(ExitCodes.Success);
        Directory.Exists(Path.Combine(Path.GetDirectoryName(solutionPath)!, ".cpmigrate_backup"))
            .Should().BeFalse("a preview that writes nothing must not leave an empty backup directory");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_NoBackupFlag_SkipsBackups()
    {
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"));

        var result = await _service.UnifyPropertiesAsync(new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = true,
            NoBackup = true,
        });

        result.Should().Be(ExitCodes.Success);
        File.Exists(Path.Combine(Path.GetDirectoryName(solutionPath)!, "Directory.Build.props"))
            .Should().BeTrue();
        Directory.Exists(Path.Combine(Path.GetDirectoryName(solutionPath)!, ".cpmigrate_backup"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task UnifyPropertiesAsync_RefusedRun_LeavesNoBackupBehind()
    {
        _console.ConfirmationResponse = false;

        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>"));

        var result = await _service.UnifyPropertiesAsync(new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Force = false,
        });

        result.Should().Be(ExitCodes.Success);
        Directory.Exists(Path.Combine(Path.GetDirectoryName(solutionPath)!, ".cpmigrate_backup"))
            .Should().BeFalse("a run that never wrote must not leave backup state behind");
    }

    // Helper methods

    private string CreateTestProject(string projectName, string content)
    {
        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, content);
        return projectPath;
    }

    private string CreateTestSolution(string solutionName, params string[] projectPaths)
    {
        var solutionPath = Path.Combine(_testDirectory, solutionName);
        var solutionContent = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
";

        var projectGuids = new List<string>();
        foreach (var projectPath in projectPaths)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpper();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(solutionPath) ?? "", projectPath);

            projectGuids.Add(projectGuid);

            solutionContent += $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{relativePath}"", ""{projectGuid}""
EndProject
";
        }

        solutionContent += @"Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
";

        foreach (var guid in projectGuids)
        {
            solutionContent += $@"        {guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {guid}.Debug|Any CPU.Build.0 = Debug|Any CPU
        {guid}.Release|Any CPU.ActiveCfg = Release|Any CPU
        {guid}.Release|Any CPU.Build.0 = Release|Any CPU
";
        }

        solutionContent += @"    EndGlobalSection
EndGlobal
";

        File.WriteAllText(solutionPath, solutionContent);
        return solutionPath;
    }
}
