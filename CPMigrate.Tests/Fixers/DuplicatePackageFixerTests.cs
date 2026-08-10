using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// Tests for DuplicatePackageFixer covering casing standardization,
/// duplicate detection, and edge cases.
/// </summary>
public class DuplicatePackageFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly DuplicatePackageFixer _fixer;

    public DuplicatePackageFixerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateDuplicateFixerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _fixer = new DuplicatePackageFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_CasingVariationsDescription_ReturnsTrue()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations in different projects",
            new[] { "Project1.csproj" },
            AnalysisIssueCode.DuplicatePackageCasing
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeTrue();
    }

    [Fact]
    public void CanFix_DifferentCasingsDescription_ReturnsTrue()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Found different casings for this package",
            new[] { "Project1.csproj" },
            AnalysisIssueCode.DuplicatePackageCasing
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
    public void Fix_CasingVariations_StandardizesToMostCommon()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "newtonsoft.json", "13.0.1");
        var project3Path = CreateTestProject("Project3.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path, project3Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj"),
            new("newtonsoft.json", "13.0.1", project2Path, "Project2.csproj"),
            new("Newtonsoft.Json", "13.0.1", project3Path, "Project3.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1); // Only Project2 needs fixing

        // Verify Project2 was updated to standard casing
        var project2Content = File.ReadAllText(project2Path);
        project2Content.Should().Contain("Newtonsoft.Json");
        project2Content.Should().NotContain("newtonsoft.json");
    }

    [Fact]
    public void Fix_CasingVariationInUpdateAttribute_StandardizesUpdate()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = Path.Combine(_testDirectory, "Project2.csproj");
        File.WriteAllText(project2Path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="newtonsoft.json" />
              </ItemGroup>
            </Project>
            """);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (updateReferences, scanSucceeded) = scanner.ScanDeclaredPackages(project2Path);
        scanSucceeded.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj")
            }.Concat(updateReferences).ToList()
        );

        // Act
        var result = _fixer.Fix(issue, packageInfo, new Options(), dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        var project2Content = File.ReadAllText(project2Path);
        project2Content.Should().Contain("Update=\"Newtonsoft.Json\"");
        project2Content.Should().NotContain("Update=\"newtonsoft.json\"");
    }

    [Fact]
    public void Fix_EmptyIncludeWithUpdate_StandardizesUpdate()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = Path.Combine(_testDirectory, "Project2.csproj");
        File.WriteAllText(project2Path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="" Update="newtonsoft.json" />
              </ItemGroup>
            </Project>
            """);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );
        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj"),
            new("newtonsoft.json", "", project2Path, "Project2.csproj")
        });

        // Act
        var result = _fixer.Fix(issue, packageInfo, new Options(), dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        var project2Content = File.ReadAllText(project2Path);
        project2Content.Should().Contain("Update=\"Newtonsoft.Json\"");
        project2Content.Should().NotContain("Update=\"newtonsoft.json\"");
    }

    [Fact]
    public void Fix_UsesDeclaredReferences_WhenResolvedGraphDoesNotExposeCasing()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "newtonsoft.json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj", IsTransitive: true),
                new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj", IsTransitive: true)
            },
            DeclaredReferences: new List<PackageReference>
            {
                new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj"),
                new("newtonsoft.json", "13.0.1", project2Path, "Project2.csproj")
            });

        // Act
        var result = _fixer.Fix(issue, packageInfo, new Options(), dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        File.ReadAllText(project2Path).Should().Contain("Newtonsoft.Json");
        File.ReadAllText(project2Path).Should().NotContain("newtonsoft.json");
    }

    [Fact]
    public void Fix_TransitiveFallbackCasing_ReturnsNoFixNeeded()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "newtonsoft.json", "13.0.1");
        var originalProject2 = File.ReadAllText(project2Path);
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );
        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj", IsTransitive: true),
            new("newtonsoft.json", "13.0.1", project2Path, "Project2.csproj", IsTransitive: true)
        });

        // Act
        var result = _fixer.Fix(issue, packageInfo, new Options(), dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No references found");
        result.Changes.Should().BeEmpty();
        File.ReadAllText(project2Path).Should().Be(originalProject2);
    }

    [Fact]
    public void Fix_MultipleCasingVariations_UsesFirstMostCommon()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "NEWTONSOFT.JSON", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "newtonsoft.json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("NEWTONSOFT.JSON", "13.0.1", project1Path, "Project1.csproj"),
            new("newtonsoft.json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1); // One project needs fixing
    }

    [Fact]
    public void Fix_NoCasingVariations_ReturnsNoFixNeeded()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No casing variations");
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Fix_NoReferencesFound_ReturnsNoFixNeeded()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "NonExistentPackage",
            "Package has casing variations",
            new[] { "Project1.csproj" }
        );

        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No references found");
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Fix_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "newtonsoft.json", "13.0.1");
        var originalContent2 = File.ReadAllText(project2Path);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj"),
            new("newtonsoft.json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        // File should not be modified in dry-run
        File.ReadAllText(project2Path).Should().Be(originalContent2);
    }

    [Fact]
    public void Fix_FileDoesNotExist_SkipsNonExistentFile()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.csproj");
        var existingPath1 = CreateTestProject("Existing1.csproj", "Newtonsoft.Json", "13.0.1");
        var existingPath2 = CreateTestProject("Existing2.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { nonExistentPath, existingPath1, existingPath2 }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("newtonsoft.json", "13.0.1", nonExistentPath, "NonExistent.csproj"),
            new("Newtonsoft.Json", "13.0.1", existingPath1, "Existing1.csproj"),
            new("Newtonsoft.Json", "13.0.1", existingPath2, "Existing2.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        // Non-existent file is skipped, no error thrown
        // Newtonsoft.Json is most common (2 vs 1), so no other files need fixing
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Fix_InvalidXml_SkipsFile()
    {
        // Arrange
        var invalidPath = Path.Combine(_testDirectory, "Invalid.csproj");
        File.WriteAllText(invalidPath, "<Project><NotClosed>");

        var validPath = CreateTestProject("Valid.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { invalidPath, validPath }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("newtonsoft.json", "13.0.1", invalidPath, "Invalid.csproj"),
            new("Newtonsoft.Json", "13.0.1", validPath, "Valid.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        // Should complete successfully, skipping invalid file
    }

    [Fact]
    public void Fix_MultiplePackagesInSameProject_FixesCorrectPackage()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Project1.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""newtonsoft.json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { projectPath }
        );

        // Create package info where "Newtonsoft.Json" is more common (2 vs 1)
        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("newtonsoft.json", "13.0.1", projectPath, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project3.csproj", "Project3.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Newtonsoft.Json");
        updatedContent.Should().NotContain("newtonsoft.json");
        updatedContent.Should().Contain("Serilog"); // Other packages unchanged
    }

    [Fact]
    public void Fix_AllReferencesAlreadyStandardized_ReturnsNoFixNeeded()
    {
        // Arrange
        var project1Path = CreateTestProject("Project1.csproj", "Newtonsoft.Json", "13.0.1");
        var project2Path = CreateTestProject("Project2.csproj", "Newtonsoft.Json", "13.0.1");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Package has casing variations",
            new[] { project1Path, project2Path }
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", project1Path, "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
        });

        var options = new Options();

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No casing variations");
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        // Assert
        _fixer.Name.Should().Be("Duplicate Package Casing Fixer");
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
