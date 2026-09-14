using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Services.Verify;
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
    public async Task UnifyPropertiesAsync_PropertyInLaterGroup_StillBecomesEffective()
    {
        // MSBuild is document-order last-wins across every PropertyGroup. A unify write that
        // only touches the first group leaves a later declaration in force — the run reports
        // success while the property keeps its old value.
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
  <PropertyGroup>
    <Nullable>disable</Nullable>
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
        File.ReadAllText(buildPropsPath).Should().NotContain("<Nullable>disable</Nullable>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ItemInConditionalGroup_NotDuplicated()
    {
        // Items are cumulative, not last-wins: leaving the conditional element and adding a
        // second one makes the item apply twice — NuGet reports the pair as a duplicate.
        var solutionPath = CreateTestSolution("TestSolution.sln",
            CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <Using Include=""System"" />
  </ItemGroup>
  <ItemGroup>
    <Using Include=""System.Text"" />
  </ItemGroup>
</Project>"),
            CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <Using Include=""System"" />
  </ItemGroup>
  <ItemGroup>
    <Using Include=""System.Text"" />
  </ItemGroup>
</Project>")
        );

        var buildPropsPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? "", "Directory.Build.props");
        File.WriteAllText(buildPropsPath, @"<Project>
  <ItemGroup>
    <Using Include=""System.Memory"" />
  </ItemGroup>
  <ItemGroup Condition=""'$(OS)' == 'Windows_NT'"">
    <Using Include=""System"" />
  </ItemGroup>
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
        var content = File.ReadAllText(buildPropsPath);
        content.Split("<Using Include=\"System\"", StringSplitOptions.None).Length.Should().Be(2);
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

    // --verify on --unify-props

    [Fact]
    public async Task UnifyPropertiesAsync_Verify_BaselineRestoreFails_WritesNothing()
    {
        // Arrange - three consensus projects, but the baseline restore reports failure
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var p3 = CreateTestProject("Project3.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);
        var baseline = Snapshot(restoreSucceeded: false, [p1, p2, p3]);

        var service = VerifyService(baseline, after: Snapshot(restoreSucceeded: true, [p1, p2, p3]));
        var options = VerifyOptions(solutionPath);

        // Act
        var result = await service.UnifyPropertiesAsync(options);

        // Assert - fail closed: no props file, no project touched
        result.Should().Be(ExitCodes.GraphDrift);
        var buildPropsPath = Path.Combine(_testDirectory, "Directory.Build.props");
        File.Exists(buildPropsPath).Should().BeFalse("a failed baseline must stop before a byte is written");
        File.ReadAllText(p1).Should().Contain("<Nullable>enable</Nullable>");
        _console.ErrorMessages.Should().Contain(m => m.Contains("does not restore before"));
    }

    [Fact]
    public async Task UnifyPropertiesAsync_Verify_UnchangedGraph_Succeeds()
    {
        // Arrange
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var p3 = CreateTestProject("Project3.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);
        var graph = Snapshot(true, [p1, p2, p3], (p1, "Newtonsoft.Json", "13.0.3"));

        var service = VerifyService(baseline: graph, after: graph);
        var options = VerifyOptions(solutionPath);

        // Act
        var result = await service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);
        File.Exists(Path.Combine(_testDirectory, "Directory.Build.props")).Should().BeTrue();
        File.ReadAllText(p1).Should().NotContain("<Nullable>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_Verify_InjectedPackageReference_IsExplainedDrift()
    {
        // Arrange - Polly is a PackageReference candidate at 2/3 consensus; the after graph shows
        // the third project now resolving it — the injection --verify exists to attribute.
        var withPolly = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Polly"" />
  </ItemGroup>
</Project>";
        var p1 = CreateTestProject("Project1.csproj", withPolly);
        var p2 = CreateTestProject("Project2.csproj", withPolly);
        var p3 = CreateTestProject("Project3.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);

        var baseline = Snapshot(true, [p1, p2, p3], (p1, "Polly", "8.0.0"), (p2, "Polly", "8.0.0"));
        var after = Snapshot(true, [p1, p2, p3], (p1, "Polly", "8.0.0"), (p2, "Polly", "8.0.0"), (p3, "Polly", "8.0.0"));

        var service = VerifyService(baseline, after);
        var options = VerifyOptions(solutionPath);
        var outputFile = Path.Combine(_testDirectory, "report.json");
        options.Output = OutputFormat.Json;
        options.OutputFile = outputFile;

        // Act
        var result = await service.UnifyPropertiesAsync(options);

        // Assert - a gain the run claimed is explained drift, not a failure
        result.Should().Be(ExitCodes.Success);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
        var verification = doc.RootElement.GetProperty("verification");
        verification.GetProperty("verdict").GetString().Should().Be("explainedDrift");
        verification.GetProperty("changes")[0].GetProperty("explanation").GetString()
            .Should().Be("unified");
        verification.GetProperty("rolledBack").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task UnifyPropertiesAsync_Verify_UnrelatedMove_RollsBack()
    {
        // Arrange - the after graph shows Serilog appearing: no candidate names it, so it is
        // unexplained drift and the pass must undo itself.
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var p3 = CreateTestProject("Project3.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);
        var originalP1 = File.ReadAllText(p1);

        var baseline = Snapshot(true, [p1, p2, p3]);
        var after = Snapshot(true, [p1, p2, p3], (p1, "Serilog", "4.0.0"));

        var service = VerifyService(baseline, after);
        var options = VerifyOptions(solutionPath);

        // Act
        var result = await service.UnifyPropertiesAsync(options);

        // Assert - drift nobody claimed: the tree goes back the way it was found
        result.Should().Be(ExitCodes.GraphDrift);
        File.ReadAllText(p1).Should().Be(originalP1);
        File.ReadAllText(p2).Should().Contain("<Nullable>enable</Nullable>");
        File.Exists(Path.Combine(_testDirectory, "Directory.Build.props"))
            .Should().BeFalse("rollback must remove the props file the run created");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_VerifyStrict_ExplainedDrift_FailsButKeepsTree()
    {
        // Arrange - same explained injection, but strict mode asks for a literal no-op
        var withPolly = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Polly"" />
  </ItemGroup>
</Project>";
        var p1 = CreateTestProject("Project1.csproj", withPolly);
        var p2 = CreateTestProject("Project2.csproj", withPolly);
        var p3 = CreateTestProject("Project3.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);

        var baseline = Snapshot(true, [p1, p2, p3], (p1, "Polly", "8.0.0"), (p2, "Polly", "8.0.0"));
        var after = Snapshot(true, [p1, p2, p3], (p1, "Polly", "8.0.0"), (p2, "Polly", "8.0.0"), (p3, "Polly", "8.0.0"));

        var service = VerifyService(baseline, after);
        var options = VerifyOptions(solutionPath);
        options.VerifyStrict = true;

        // Act
        var result = await service.UnifyPropertiesAsync(options);

        // Assert - strict fails on explainable drift, but the tree is left to be read: explained
        // drift does not roll back.
        result.Should().Be(ExitCodes.GraphDrift);
        File.Exists(Path.Combine(_testDirectory, "Directory.Build.props")).Should().BeTrue();
        File.ReadAllText(p1).Should().NotContain("<PackageReference Include=\"Polly\"");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_Json_CandidatesCarryWillGain()
    {
        // Arrange - Nullable at 2/3 consensus: one project does not declare it and will gain it
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var p3 = CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);
        var outputFile = Path.Combine(_testDirectory, "report.json");

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            Force = true,
            Output = OutputFormat.Json,
            OutputFile = outputFile,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
        var nullable = doc.RootElement
            .GetProperty("candidates")
            .GetProperty("properties")
            .EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "Nullable");
        nullable.GetProperty("willGain").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UnifyPropertiesAsync_VariantHolder_WarnsAboutDuplicateItem()
    {
        // Arrange - two projects hold 'Using System' bare; a third holds it under Alias metadata.
        // The variant keeps its own copy AND inherits the props one — the NU1504-shaped hazard the
        // run must name before writing.
        var bare = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System"" />
  </ItemGroup>
</Project>";
        var variant = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System"" Alias=""Sys"" />
  </ItemGroup>
</Project>";
        var p1 = CreateTestProject("Project1.csproj", bare);
        var p2 = CreateTestProject("Project2.csproj", bare);
        var p3 = CreateTestProject("Project3.csproj", variant);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            DryRun = true,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m =>
            m.Contains("different metadata") && m.Contains("duplicate"));
    }

    [Fact]
    public async Task UnifyPropertiesAsync_Json_FilesModifiedCountsRealWrites()
    {
        // Arrange - Nullable held by 2 of 3 projects, and the third on a different TFM so nothing
        // it declares is a candidate: the run writes the props file plus the two holders —
        // reporting projectPaths.Count + 1 would claim a fourth file nobody touched.
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var p3 = CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);
        var outputFile = Path.Combine(_testDirectory, "report.json");

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            Force = true,
            Output = OutputFormat.Json,
            OutputFile = outputFile,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
        doc.RootElement.GetProperty("summary").GetProperty("filesModified").GetInt32()
            .Should().Be(3, "the props file plus the two projects that actually held the property");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_Json_ItemProjectsListExactHoldersOnly()
    {
        // Arrange - 'Using System' bare in two projects, under Alias metadata in a third. The
        // variant holder declares a different thing and must not appear in the candidate's
        // projects list — otherwise projects.Length disagrees with count.
        var bare = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System"" />
  </ItemGroup>
</Project>";
        var variant = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System"" Alias=""Sys"" />
  </ItemGroup>
</Project>";
        var p1 = CreateTestProject("Project1.csproj", bare);
        var p2 = CreateTestProject("Project2.csproj", bare);
        var p3 = CreateTestProject("Project3.csproj", variant);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);
        var outputFile = Path.Combine(_testDirectory, "report.json");

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            Force = true,
            Output = OutputFormat.Json,
            OutputFile = outputFile,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
        var item = doc.RootElement
            .GetProperty("candidates")
            .GetProperty("items")
            .EnumerateArray()
            .Single(e => e.GetProperty("include").GetString() == "System"
                && !e.GetProperty("metadata").EnumerateObject().Any());
        var projects = item.GetProperty("projects").EnumerateArray().Select(e => e.GetString()).ToList();
        item.GetProperty("count").GetInt32().Should().Be(2);
        projects.Should().BeEquivalentTo([p1, p2], "a variant holder is not a declarer of the candidate");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_ConditionalHolder_WarnsAboutDuplicateItem()
    {
        // Arrange - 'Using System' bare in two projects, conditional in a third. A conditional
        // item is never a consensus member, so nothing strips it — and when its condition holds
        // the project sees both copies. The hazard warning must see it too.
        var bare = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System"" />
  </ItemGroup>
</Project>";
        var conditional = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System"" Condition=""'$(OS)' == 'Windows_NT'"" />
  </ItemGroup>
</Project>";
        var p1 = CreateTestProject("Project1.csproj", bare);
        var p2 = CreateTestProject("Project2.csproj", bare);
        var p3 = CreateTestProject("Project3.csproj", conditional);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2, p3);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            DryRun = true,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert
        result.Should().Be(ExitCodes.Success);
        _console.OutputMessages.Should().Contain(m =>
            m.Contains("condition") && m.Contains("duplicate"));
    }

    // --dry-run preview

    [Fact]
    public async Task UnifyPropertiesAsync_DryRun_RendersThePropsFileItWouldWrite()
    {
        // Arrange
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            DryRun = true,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert - the preview is the real content, and nothing landed on disk
        result.Should().Be(ExitCodes.Success);
        _console.PropsPreviews.Should().ContainSingle()
            .Which.Should().Contain("<Nullable>enable</Nullable>");
        File.Exists(Path.Combine(_testDirectory, "Directory.Build.props")).Should().BeFalse();
        File.ReadAllText(p1).Should().Contain("<Nullable>enable</Nullable>");
    }

    [Fact]
    public async Task UnifyPropertiesAsync_DryRunDiff_PrintsUnifiedDiffs()
    {
        // Arrange
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2);

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            DryRun = true,
            Diff = true,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert - diff mode prints the patch instead of the preview, and it covers both sides of
        // the pass: the props file created and the projects stripped.
        result.Should().Be(ExitCodes.Success);
        _console.PropsPreviews.Should().BeEmpty("--diff replaces the content preview");
        _console.OutputMessages.Should().Contain(m => m.Contains("--- a/Directory.Build.props"));
        _console.OutputMessages.Should().Contain(m => m.Contains("--- a/Project1.csproj"));
        _console.OutputMessages.Should().Contain(m => m.Contains("-    <Nullable>enable</Nullable>"));
        File.Exists(Path.Combine(_testDirectory, "Directory.Build.props")).Should().BeFalse();
    }

    [Fact]
    public async Task UnifyPropertiesAsync_DryRunDiffFile_WritesThePatchArtifact()
    {
        // Arrange
        var p1 = CreateTestProject("Project1.csproj", ProjectWithNullable);
        var p2 = CreateTestProject("Project2.csproj", ProjectWithNullable);
        var solutionPath = CreateTestSolution("TestSolution.sln", p1, p2);
        var diffFile = Path.Combine(_testDirectory, "unify.patch");

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            DryRun = true,
            DiffFile = diffFile,
        };

        // Act
        var result = await _service.UnifyPropertiesAsync(options);

        // Assert - the artifact exists even without --diff, and captures every file the pass
        // would touch.
        result.Should().Be(ExitCodes.Success);
        var patch = await File.ReadAllTextAsync(diffFile);
        patch.Should().Contain("--- a/Directory.Build.props");
        patch.Should().Contain("--- a/Project1.csproj");
        patch.Should().Contain("--- a/Project2.csproj");
        patch.Should().Contain("-    <Nullable>enable</Nullable>");
    }

    private const string ProjectWithNullable = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

    private Options VerifyOptions(string solutionPath) =>
        new()
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? _testDirectory,
            Force = true,
            Verify = true,
            BackupDir = _testDirectory,
            Quiet = true,
        };

    private BuildPropsService VerifyService(GraphSnapshotResult baseline, GraphSnapshotResult after) =>
        new(
            _console,
            _projectAnalyzer,
            backupManager: null,
            verifier: new MigrationVerifier(new OrderedSnapshots(baseline, after)),
            rollbackHandler: new RollbackHandler(_console, quietMode: true)
        );

    /// <summary>
    /// A snapshot covering every project — <see cref="GraphDiff"/> refuses to compare two snapshots
    /// over different project sets, so each test graph names the whole solution.
    /// </summary>
    private static GraphSnapshotResult Snapshot(
        bool restoreSucceeded,
        string[] projectPaths,
        params (string ProjectPath, string PackageId, string Version)[] packages) =>
        new(
            restoreSucceeded,
            restoreSucceeded ? "restore ok" : "error NU1101: package not found",
            new ResolvedGraphSnapshot(
                restoreSucceeded
                    ? projectPaths
                        .Select(path => new ProjectResolvedGraph(
                            path,
                            [
                                new ResolvedFramework(
                                    "net8.0",
                                    Resolved: true,
                                    packages
                                        .Where(p => p.ProjectPath == path)
                                        .Select(p => new ResolvedPackage(p.PackageId, p.Version, IsDirect: true))
                                        .ToList()
                                ),
                            ]
                        ))
                        .ToList()
                    : [],
                []
            )
        );

    /// <summary>Answers the two captures a verify pass makes, in order.</summary>
    private sealed class OrderedSnapshots(GraphSnapshotResult baseline, GraphSnapshotResult after)
        : IGraphSnapshotService
    {
        private int _calls;

        public Task<GraphSnapshotResult> CaptureAsync(
            string restoreTargetPath,
            IReadOnlyList<string> projectPaths,
            string? basePath) =>
            Task.FromResult(_calls++ == 0 ? baseline : after);
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
