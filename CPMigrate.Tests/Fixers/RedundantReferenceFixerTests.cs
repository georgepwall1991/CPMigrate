using CPMigrate.Fixers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// Tests for RedundantReferenceFixer covering duplicate removal,
/// edge cases, and error handling.
/// </summary>
public class RedundantReferenceFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly RedundantReferenceFixer _fixer;

    public RedundantReferenceFixerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateRedundantFixerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _fixer = new RedundantReferenceFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_RedundantDescription_ReturnsTrue()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "Project1.csproj" },
            AnalysisIssueCode.RedundantReference
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeTrue();
    }

    [Fact]
    public void CanFix_AppearsTimesDescription_ReturnsTrue()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package appears 3 times in same project",
            new[] { "Project1.csproj" },
            AnalysisIssueCode.RedundantReference
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeTrue();
    }

    [Fact]
    public void CanFix_OtherDescription_ReturnsFalse()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Some other issue",
            new[] { "Project1.csproj" }
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeFalse();
    }

    [Fact]
    public void Fix_MultipleDuplicates_RemovesAllButFirst()
    {
        // Arrange
        var projectPath = CreateProjectWithDuplicates("Project1.csproj", "Newtonsoft.Json", 3);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", projectPath, "Project1.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        result.Changes[0].Before.Should().Be("3 references");
        result.Changes[0].After.Should().Be("1 reference");

        // Verify file content - should have only 1 reference now
        var content = File.ReadAllText(projectPath);
        var refCount = CountPackageReferences(content, "Newtonsoft.Json");
        refCount.Should().Be(1);
    }

    [Fact]
    public void Fix_TwoDuplicates_RemovesSecond()
    {
        // Arrange
        var projectPath = CreateProjectWithDuplicates("Project1.csproj", "Newtonsoft.Json", 2);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package appears 2 times",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", projectPath, "Project1.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        var content = File.ReadAllText(projectPath);
        var refCount = CountPackageReferences(content, "Newtonsoft.Json");
        refCount.Should().Be(1);
    }

    [Fact]
    public void Fix_SingleReference_ReturnsNoFixNeeded()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", projectPath, "Project1.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No redundant references found");
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Fix_NoReferencesFound_ReturnsNoFixNeeded()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "NonExistentPackage",
            "Package has redundant references",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No redundant references found");
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Fix_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        var projectPath = CreateProjectWithDuplicates("Project1.csproj", "Newtonsoft.Json", 3);
        var originalContent = File.ReadAllText(projectPath);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", projectPath, "Project1.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        // File should not be modified in dry-run
        File.ReadAllText(projectPath).Should().Be(originalContent);
        var refCount = CountPackageReferences(originalContent, "Newtonsoft.Json");
        refCount.Should().Be(3); // Still 3 duplicates
    }

    [Fact]
    public void Fix_FileDoesNotExist_SkipsNonExistentFile()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.csproj");
        var existingPath = CreateProjectWithDuplicates("Existing.csproj", "Newtonsoft.Json", 2);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "NonExistent.csproj", "Existing.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", nonExistentPath, "NonExistent.csproj"),
            new("Newtonsoft.Json", "13.0.1", existingPath, "Existing.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        // Non-existent file is skipped, only existing file processed
        result.Changes.Should().HaveCount(1);
        result.Changes[0].FilePath.Should().Be(existingPath);
    }

    [Fact]
    public void Fix_InvalidXml_SkipsFile()
    {
        // Arrange
        var invalidPath = Path.Combine(_testDirectory, "Invalid.csproj");
        File.WriteAllText(invalidPath, "<Project><NotClosed>");

        var validPath = CreateProjectWithDuplicates("Valid.csproj", "Newtonsoft.Json", 2);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "Invalid.csproj", "Valid.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", invalidPath, "Invalid.csproj"),
            new("Newtonsoft.Json", "13.0.1", validPath, "Valid.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        // Invalid file skipped, only valid file processed
        result.Changes.Should().HaveCount(1);
        result.Changes[0].FilePath.Should().Be(validPath);
    }

    [Fact]
    public void Fix_MixedPackages_OnlyRemovesDuplicatesOfTargetPackage()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Project1.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", projectPath, "Project1.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        var updatedContent = File.ReadAllText(projectPath);
        // Should have 1 Newtonsoft.Json reference
        CountPackageReferences(updatedContent, "Newtonsoft.Json").Should().Be(1);
        // Should still have 2 Serilog references (untouched)
        CountPackageReferences(updatedContent, "Serilog").Should().Be(2);
    }

    [Fact]
    public void Fix_CaseInsensitiveMatching_RemovesDuplicatesRegardlessOfCasing()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Project1.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""newtonsoft.json"" Version=""13.0.1"" />
    <PackageReference Include=""NEWTONSOFT.JSON"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has redundant references",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", projectPath, "Project1.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        result.Changes[0].Before.Should().Be("3 references");
        result.Changes[0].After.Should().Be("1 reference");

        // Count total PackageReference elements in updated content
        var updatedContent = File.ReadAllText(projectPath);
        var totalPackageRefs = System.Text.RegularExpressions.Regex.Matches(
            updatedContent,
            "<PackageReference",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        totalPackageRefs.Should().Be(1);
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        // Assert
        _fixer.Name.Should().Be("Redundant Reference Fixer");
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

    private string CreateProjectWithDuplicates(string projectName, string packageName, int duplicateCount)
    {
        var packageRefs = string.Join(Environment.NewLine,
            Enumerable.Repeat($"    <PackageReference Include=\"{packageName}\" Version=\"13.0.1\" />", duplicateCount));

        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
{packageRefs}
  </ItemGroup>
</Project>";

        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, content);
        return projectPath;
    }

    private static int CountPackageReferences(string content, string packageName)
    {
        var pattern = $"Include=\"{packageName}\"";
        return System.Text.RegularExpressions.Regex.Matches(content, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
    }
}
