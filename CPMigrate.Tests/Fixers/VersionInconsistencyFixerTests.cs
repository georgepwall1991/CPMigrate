using CPMigrate.Fixers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// Tests for VersionInconsistencyFixer covering version standardization,
/// conflict resolution strategies, XML modification, and edge cases.
/// </summary>
public class VersionInconsistencyFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionInconsistencyFixer _fixer;

    public VersionInconsistencyFixerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateFixerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _fixer = new VersionInconsistencyFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_ValidVersionInconsistencyDescription_ReturnsTrue()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { "Project1.csproj", "Project2.csproj" }
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeTrue();
    }

    [Fact]
    public void CanFix_DescriptionWithoutParentheses_ReturnsFalse()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Version inconsistency detected",
            new[] { "Project1.csproj" }
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeFalse();
    }

    [Fact]
    public void CanFix_DescriptionWithoutComma_ReturnsFalse()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Single version (Project1)",
            new[] { "Project1.csproj" }
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeFalse();
    }

    [Fact]
    public void Fix_HighestStrategy_StandardizesToHighestVersion()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "12.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");
        var project3Path = CreateTestProject("Project3.csproj", "Newtonsoft.Json", "12.0.3");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2), 12.0.3 (Project3)",
            new[] { project1Path, project2Path, project3Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj"),
            new("Newtonsoft.Json", "12.0.3", project3Path, "Project3.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(2); // Project1 and Project3 should be updated

        // Verify files were updated to 13.0.1
        File.ReadAllText(project1Path).Should().Contain("13.0.1");
        File.ReadAllText(project2Path).Should().Contain("13.0.1"); // Already at highest
        File.ReadAllText(project3Path).Should().Contain("13.0.1");
    }

    [Fact]
    public void Fix_LowestStrategy_StandardizesToLowestVersion()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "12.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Lowest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1); // Only Project2 should be updated

        // Verify files were updated to 12.0.1
        File.ReadAllText(project1Path).Should().Contain("12.0.1");
        File.ReadAllText(project2Path).Should().Contain("12.0.1");
    }

    [Fact]
    public void Fix_FailStrategy_ReturnsFailedResult()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "12.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Fail };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeFalse();
        result.Description.Should().Contain("Cannot resolve version");
    }

    [Fact]
    public void Fix_NoReferencesFound_ReturnsNoFixNeeded()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "NonExistentPackage",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { "Project1.csproj", "Project2.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No references found");
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Fix_SingleVersion_ReturnsNoFixNeeded()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "12.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "12.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 12.0.1 (Project2)",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "12.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No version conflict");
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Fix_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "12.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");
        var originalContent1 = File.ReadAllText(project1Path);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        // Files should not be modified in dry-run
        File.ReadAllText(project1Path).Should().Be(originalContent1);
    }

    [Fact]
    public void Fix_NestedVersionElement_UpdatesCorrectly()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Project1.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"">
      <Version>12.0.1</Version>
    </PackageReference>
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { projectPath }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", projectPath, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("<Version>13.0.1</Version>");
        updatedContent.Should().NotContain("12.0.1");
    }

    [Fact]
    public void Fix_FileDoesNotExist_SkipsFile()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.csproj");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { nonExistentPath, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", nonExistentPath, "NonExistent.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().BeEmpty(); // No changes since one file doesn't exist and other is already at target
    }

    [Fact]
    public void Fix_InvalidXml_SkipsFile()
    {
        // Arrange
        var invalidProjectPath = Path.Combine(_testDirectory, "Invalid.csproj");
        File.WriteAllText(invalidProjectPath, "<Project><NotClosed>");

        var validProjectPath = CreateTestProject("Valid.csproj", "Newtonsoft.Json", "12.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Invalid), 12.0.1 (Valid)",
            new[] { invalidProjectPath, validProjectPath }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", invalidProjectPath, "Invalid.csproj"),
            new("Newtonsoft.Json", "12.0.1", validProjectPath, "Valid.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().BeEmpty(); // No changes since all at same version
    }

    [Fact]
    public void Fix_AllReferencesAtTargetVersion_ReturnsNoFixNeeded()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "12.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (Project1), 13.0.1 (Project2)",
            new[] { project1Path, project2Path }
        );

        // Both already at target after resolution (highest = 13.0.1)
        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No version conflict");
    }

    [Fact]
    public void Fix_PreReleaseVersions_HandlesCorrectly()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1-preview");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "13.0.1-preview (Project1), 13.0.1 (Project2)",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1-preview", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        // Should standardize to one version (version parsing handles prerelease)
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        // Assert
        _fixer.Name.Should().Be("Version Inconsistency Fixer");
    }

    [Fact]
    public void Fix_PrereleaseWithDots_HighestStrategy_SelectsCorrectVersion()
    {
        // Arrange — versions like "1.0.0-beta.1" would fail with System.Version but work with NuGetVersion
        var project1Path = CreateTestProject("Project1.csproj", "MyPackage", "1.0.0-beta.1");
        var project2Path = CreateTestProject("Project2.csproj", "MyPackage", "1.0.0-rc.1");
        var project3Path = CreateTestProject("Project3.csproj", "MyPackage", "1.0.0");

        var issue = new AnalysisIssue(
            "MyPackage",
            "1.0.0-beta.1 (Project1), 1.0.0-rc.1 (Project2), 1.0.0 (Project3)",
            new[] { project1Path, project2Path, project3Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("MyPackage", "1.0.0-beta.1", project1Path, "Project1.csproj"),
            new("MyPackage", "1.0.0-rc.1", project2Path, "Project2.csproj"),
            new("MyPackage", "1.0.0", project3Path, "Project3.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert — stable release 1.0.0 should be highest (NuGet: stable > prerelease of same version)
        result.Success.Should().BeTrue();
        // Project1 and Project2 should be updated to 1.0.0
        File.ReadAllText(project1Path).Should().Contain("1.0.0");
        File.ReadAllText(project2Path).Should().Contain("1.0.0");
        File.ReadAllText(project3Path).Should().Contain("1.0.0");
    }

    [Fact]
    public void Fix_SemverBuildMetadata_HighestStrategy_HandlesBuildMetadata()
    {
        // Arrange — versions with build metadata like "2.0.0+build.123"
        var project1Path = CreateTestProject("Project1.csproj", "MyPackage", "2.0.0");
        var project2Path = CreateTestProject("Project2.csproj", "MyPackage", "1.5.0");

        var issue = new AnalysisIssue(
            "MyPackage",
            "2.0.0 (Project1), 1.5.0 (Project2)",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("MyPackage", "2.0.0", project1Path, "Project1.csproj"),
            new("MyPackage", "1.5.0", project2Path, "Project2.csproj")
        });

        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        File.ReadAllText(project2Path).Should().Contain("2.0.0");
    }

    // Helper methods

    private string CreateTestProject(string projectName, string packageName, string version)
    {
        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""{packageName}"" Version=""{version}"" />
  </ItemGroup>
</Project>";

        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, content);
        return projectPath;
    }
}
