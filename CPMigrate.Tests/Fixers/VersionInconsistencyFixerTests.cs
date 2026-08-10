using CPMigrate.Analyzers;
using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services;
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
            new[] { "Project1.csproj", "Project2.csproj" },
            AnalysisIssueCode.VersionInconsistency
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
    public void Fix_UpdateOnlyReference_UpdatesVersionAttribute()
    {
        // Arrange: the scanner reports an Update-only declaration as a real package reference, so the
        // analyzer can produce a fixable cross-project version finding for it.
        var updateOnlyPath = CreateUpdateOnlyProject("UpdateOnly.csproj", "Newtonsoft.Json", "12.0.1");
        var includePath = CreateTestProject("Include.csproj", "Newtonsoft.Json", "13.0.1");
        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (updateReferences, updateScanSucceeded) = scanner.ScanDeclaredPackages(updateOnlyPath);
        var (includeReferences, includeScanSucceeded) = scanner.ScanDeclaredPackages(includePath);

        updateScanSucceeded.Should().BeTrue();
        includeScanSucceeded.Should().BeTrue();
        updateReferences.Should().ContainSingle();

        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                // Production analysis supplies resolved references here; the declared scan is carried
                // separately so conditional filtering can still distinguish intentional project-file pins.
                new("Newtonsoft.Json", "12.0.1", updateOnlyPath, "UpdateOnly.csproj"),
                new("Newtonsoft.Json", "13.0.1", includePath, "Include.csproj"),
            },
            DeclaredReferences: updateReferences.Concat(includeReferences).ToList()
        );
        var issue = new VersionInconsistencyAnalyzer().Analyze(packageInfo).Issues.Should().ContainSingle().Subject;

        // Act
        var result = _fixer.Fix(
            issue,
            packageInfo,
            new Options { ConflictStrategy = ConflictStrategy.Highest },
            dryRun: false
        );

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(updateOnlyPath);
        updatedContent.Should().Contain("Update=\"Newtonsoft.Json\" Version=\"13.0.1\"");
        updatedContent.Should().NotContain("Version=\"12.0.1\"");
    }

    [Fact]
    public void Fix_UpdateBeforeInclude_UpdatesPotentialInheritedUpdate()
    {
        var projectPath = Path.Combine(_testDirectory, "Project1.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Newtonsoft.Json" Version="12.0.1" />
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (Project1), 2.0.0 (Project2)",
            new[] { projectPath, "Project2.csproj" }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "Project1.csproj"),
                new("Newtonsoft.Json", "2.0.0", "Project2.csproj", "Project2.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("Update=\"Newtonsoft.Json\" Version=\"2.0.0\"");
        content.Should().Contain("Include=\"Newtonsoft.Json\" Version=\"2.0.0\"");
    }

    [Fact]
    public void Fix_UpdateBeforeConditionalInclude_UpdatesUnconditionalUpdateOnly()
    {
        var projectPath = Path.Combine(_testDirectory, "ConditionalProject.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Newtonsoft.Json" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Newtonsoft.Json" Version="9.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (ConditionalProject), 2.0.0 (Project2)",
            new[] { projectPath, "Project2.csproj" }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "ConditionalProject.csproj"),
                new("Newtonsoft.Json", "2.0.0", "Project2.csproj", "Project2.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("Update=\"Newtonsoft.Json\" Version=\"2.0.0\"");
        content.Should().Contain("Include=\"Newtonsoft.Json\" Version=\"9.0.0\"");
    }

    [Fact]
    public void Fix_ImportedOverrideClearPreservesUnconditionalVersion()
    {
        var projectPath = Path.Combine(_testDirectory, "ImportedOverride.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Newtonsoft.Json" Version="3.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Newtonsoft.Json" VersionOverride="" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherImportedOverride.csproj", "Newtonsoft.Json", "4.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "3.0.0 (ImportedOverride.csproj), 4.0.0 (OtherImportedOverride.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "3.0.0", projectPath, "ImportedOverride.csproj"),
                new("Newtonsoft.Json", "4.0.0", otherPath, "OtherImportedOverride.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().BeEmpty();
        File.ReadAllText(projectPath).Should().Contain("Version=\"3.0.0\"");
    }

    [Fact]
    public void Fix_MultiItemUpdateDeclinesUnrelatedPackageChange()
    {
        var projectPath = Path.Combine(_testDirectory, "MultiItemUpdate.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Foo;Bar" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherMultiItemUpdate.csproj", "Foo", "3.0.0");
        var issue = new AnalysisIssue(
            "Foo",
            "2.0.0 (MultiItemUpdate.csproj), 3.0.0 (OtherMultiItemUpdate.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "2.0.0", projectPath, "MultiItemUpdate.csproj"),
                new("Foo", "3.0.0", otherPath, "OtherMultiItemUpdate.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        result.Description.Should().Contain("multiple packages");
        File.ReadAllText(projectPath).Should().Contain("Update=\"Foo;Bar\" Version=\"2.0.0\"");
        File.ReadAllText(otherPath).Should().Contain("Version=\"3.0.0\"");
    }

    [Fact]
    public void Fix_MetadataOnlySharedUpdateDoesNotBlockVersionChange()
    {
        var projectPath = Path.Combine(_testDirectory, "MetadataOnlySharedUpdate.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Foo;Bar" PrivateAssets="all" />
                <PackageReference Include="Foo" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherMetadataOnlySharedUpdate.csproj", "Foo", "3.0.0");
        var issue = new AnalysisIssue(
            "Foo",
            "2.0.0 (MetadataOnlySharedUpdate.csproj), 3.0.0 (OtherMetadataOnlySharedUpdate.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "2.0.0", projectPath, "MetadataOnlySharedUpdate.csproj"),
                new("Foo", "3.0.0", otherPath, "OtherMetadataOnlySharedUpdate.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        File.ReadAllText(projectPath).Should().Contain("Update=\"Foo;Bar\" PrivateAssets=\"all\"");
        File.ReadAllText(projectPath).Should().Contain("Version=\"3.0.0\"");
    }

    [Fact]
    public void Fix_UnconditionalVersionCanChangeBesideConditionalMetadata()
    {
        var projectPath = Path.Combine(_testDirectory, "UnconditionalBesideConditionalMetadata.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Foo" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Foo">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">1.0.0</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherUnconditionalBesideConditionalMetadata.csproj", "Foo", "3.0.0");
        var issue = new AnalysisIssue(
            "Foo",
            "2.0.0 (UnconditionalBesideConditionalMetadata.csproj), 3.0.0 (OtherUnconditionalBesideConditionalMetadata.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "2.0.0", projectPath, "UnconditionalBesideConditionalMetadata.csproj"),
                new("Foo", "3.0.0", otherPath, "OtherUnconditionalBesideConditionalMetadata.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Include=\"Foo\" Version=\"3.0.0\"");
        updatedContent.Should().Contain(
            "<Version Condition=\"'$(TargetFramework)' == 'net8.0'\">1.0.0</Version>"
        );
    }

    [Fact]
    public void Fix_MixedDeclarationUpdatesUnconditionalMetadataOnly()
    {
        var projectPath = Path.Combine(_testDirectory, "MixedDeclaration.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Foo" Version="2.0.0">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">1.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherMixedDeclaration.csproj", "Foo", "3.0.0");
        var issue = new AnalysisIssue(
            "Foo",
            "2.0.0 (MixedDeclaration.csproj), 3.0.0 (OtherMixedDeclaration.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "2.0.0", projectPath, "MixedDeclaration.csproj"),
                new("Foo", "3.0.0", otherPath, "OtherMixedDeclaration.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Include=\"Foo\" Version=\"3.0.0\">");
        updatedContent.Should().Contain(
            "<VersionOverride Condition=\"'$(TargetFramework)' == 'net8.0'\">1.0.0</VersionOverride>"
        );
    }

    [Fact]
    public void Fix_ConditionedOverrideDoesNotSupersedeUnconditionalPropertyOverride()
    {
        var projectPath = Path.Combine(_testDirectory, "ConditionedOverrideSupersession.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Foo" VersionOverride="$(FooVersion)" />
                <PackageReference Update="Foo" Version="2.0.0">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">3.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject(
            "OtherConditionedOverrideSupersession.csproj",
            "Foo",
            "1.0.0"
        );
        var issue = new AnalysisIssue(
            "Foo",
            "1.0.0 (ConditionedOverrideSupersession.csproj), 2.0.0 (OtherConditionedOverrideSupersession.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "1.0.0", projectPath, "ConditionedOverrideSupersession.csproj"),
                new("Foo", "2.0.0", otherPath, "OtherConditionedOverrideSupersession.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("VersionOverride=\"$(FooVersion)\"");
        content.Should().Contain(
            "<VersionOverride Condition=\"'$(TargetFramework)' == 'net8.0'\">3.0.0</VersionOverride>"
        );
        content.Should().Contain("Version=\"2.0.0\"");
    }

    [Fact]
    public void Fix_ConditionedVersionMetadataDeclinesRewrite()
    {
        var projectPath = Path.Combine(_testDirectory, "ConditionedMetadata.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Foo">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">2.0.0</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherConditionedMetadata.csproj", "Foo", "3.0.0");
        var issue = new AnalysisIssue(
            "Foo",
            "2.0.0 (ConditionedMetadata.csproj), 3.0.0 (OtherConditionedMetadata.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "2.0.0", projectPath, "ConditionedMetadata.csproj"),
                new("Foo", "3.0.0", otherPath, "OtherConditionedMetadata.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        File.ReadAllText(projectPath).Should().Contain(">2.0.0</Version>");
        File.ReadAllText(otherPath).Should().Contain("Version=\"3.0.0\"");
    }

    [Fact]
    public void Fix_UpdateOnlyVersionOverride_UpdatesVersionOverrideAttribute()
    {
        // Arrange: VersionOverride is the effective project-level pin under central package management,
        // even though the resolved reference supplies the concrete version to the analyzer.
        var updateOnlyPath = CreateUpdateOnlyVersionOverrideProject(
            "UpdateOnlyOverride.csproj",
            "Newtonsoft.Json",
            "12.0.1"
        );
        var includePath = CreateTestProject("IncludeOverride.csproj", "Newtonsoft.Json", "13.0.1");
        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (updateReferences, updateScanSucceeded) = scanner.ScanDeclaredPackages(updateOnlyPath);
        var (includeReferences, includeScanSucceeded) = scanner.ScanDeclaredPackages(includePath);

        updateScanSucceeded.Should().BeTrue();
        includeScanSucceeded.Should().BeTrue();
        updateReferences.Should().ContainSingle();

        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", updateOnlyPath, "UpdateOnlyOverride.csproj"),
                new("Newtonsoft.Json", "13.0.1", includePath, "IncludeOverride.csproj"),
            },
            DeclaredReferences: updateReferences.Concat(includeReferences).ToList()
        );
        var issue = new VersionInconsistencyAnalyzer().Analyze(packageInfo).Issues.Should().ContainSingle().Subject;

        // Act
        var result = _fixer.Fix(
            issue,
            packageInfo,
            new Options { ConflictStrategy = ConflictStrategy.Highest },
            dryRun: false
        );

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(updateOnlyPath);
        updatedContent.Should().Contain("Update=\"Newtonsoft.Json\" VersionOverride=\"13.0.1\"");
        updatedContent.Should().NotContain("VersionOverride=\"12.0.1\"");
    }

    [Fact]
    public void Fix_PropertyVersion_IsNotOverwritten()
    {
        var propertyPath = Path.Combine(_testDirectory, "PropertyVersion.csproj");
        File.WriteAllText(
            propertyPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="$(NewtonsoftVersion)" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherPropertyVersion.csproj", "Newtonsoft.Json", "13.0.1");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (PropertyVersion.csproj), 13.0.1 (OtherPropertyVersion.csproj)",
            new[] { propertyPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", propertyPath, "PropertyVersion.csproj"),
                new("Newtonsoft.Json", "13.0.1", otherPath, "OtherPropertyVersion.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new Options { ConflictStrategy = ConflictStrategy.Highest },
            dryRun: false
        );

        result.Success.Should().BeFalse();
        File.ReadAllText(propertyPath).Should().Contain("Version=\"$(NewtonsoftVersion)\"");
        File.ReadAllText(propertyPath).Should().NotContain("Version=\"13.0.1\"");
    }

    [Fact]
    public void Fix_ItemVersionExpression_IsNotOverwritten()
    {
        var expressionPath = Path.Combine(_testDirectory, "ItemVersionExpression.csproj");
        File.WriteAllText(
            expressionPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="@(SelectedVersion)" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherItemVersionExpression.csproj", "Newtonsoft.Json", "13.0.1");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "12.0.1 (ItemVersionExpression.csproj), 13.0.1 (OtherItemVersionExpression.csproj)",
            new[] { expressionPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", expressionPath, "ItemVersionExpression.csproj"),
                new("Newtonsoft.Json", "13.0.1", otherPath, "OtherItemVersionExpression.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeFalse();
        File.ReadAllText(expressionPath).Should().Contain("Version=\"@(SelectedVersion)\"");
        File.ReadAllText(expressionPath).Should().NotContain("Version=\"13.0.1\"");
    }

    [Fact]
    public void Fix_ExplicitEmptyUpdateMetadata_IsNotOverwritten()
    {
        var projectPath = Path.Combine(_testDirectory, "ExplicitEmptyUpdateMetadata.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Update="Newtonsoft.Json" Version="" VersionOverride="" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherExplicitEmptyUpdateMetadata.csproj", "Newtonsoft.Json", "2.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (ExplicitEmptyUpdateMetadata.csproj), 2.0.0 (OtherExplicitEmptyUpdateMetadata.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "ExplicitEmptyUpdateMetadata.csproj"),
                new("Newtonsoft.Json", "2.0.0", otherPath, "OtherExplicitEmptyUpdateMetadata.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("Version=\"\"");
        content.Should().Contain("VersionOverride=\"\"");
        content.Should().NotContain("Version=\"2.0.0\"");
        content.Should().NotContain("VersionOverride=\"2.0.0\"");
    }

    [Fact]
    public void Fix_UnconditionalEmptyVersionClearDeclinesRewrite()
    {
        var projectPath = Path.Combine(_testDirectory, "UnconditionalEmptyVersionClear.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Foo" Version="1.0.0" />
                <PackageReference Update="Foo" Version="" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherUnconditionalEmptyVersionClear.csproj", "Foo", "3.0.0");
        var issue = new AnalysisIssue(
            "Foo",
            "2.0.0 (UnconditionalEmptyVersionClear.csproj), 3.0.0 (OtherUnconditionalEmptyVersionClear.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "2.0.0", projectPath, "UnconditionalEmptyVersionClear.csproj"),
                new("Foo", "3.0.0", otherPath, "OtherUnconditionalEmptyVersionClear.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("Include=\"Foo\" Version=\"1.0.0\"");
        content.Should().Contain("Update=\"Foo\" Version=\"\"");
    }

    [Fact]
    public void Fix_InheritedEmptyVersionClearDeclinesRewrite()
    {
        var projectPath = Path.Combine(_testDirectory, "InheritedEmptyVersionClear.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Foo" Version="" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherInheritedEmptyVersionClear.csproj", "Foo", "3.0.0");
        var issue = new AnalysisIssue(
            "Foo",
            "2.0.0 (InheritedEmptyVersionClear.csproj), 3.0.0 (OtherInheritedEmptyVersionClear.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Foo", "2.0.0", projectPath, "InheritedEmptyVersionClear.csproj"),
                new("Foo", "3.0.0", otherPath, "OtherInheritedEmptyVersionClear.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        File.ReadAllText(projectPath).Should().Contain("Update=\"Foo\" Version=\"\"");
    }

    [Fact]
    public void Fix_LiteralUpdateSupersedesEarlierPropertyVersion()
    {
        var projectPath = Path.Combine(_testDirectory, "SupersededPropertyVersion.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="$(NewtonsoftVersion)" />
                <PackageReference Update="Newtonsoft.Json" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherSupersededPropertyVersion.csproj", "Newtonsoft.Json", "2.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (SupersededPropertyVersion.csproj), 2.0.0 (OtherSupersededPropertyVersion.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "SupersededPropertyVersion.csproj"),
                new("Newtonsoft.Json", "2.0.0", otherPath, "OtherSupersededPropertyVersion.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Version=\"$(NewtonsoftVersion)\"");
        updatedContent.Should().Contain("Update=\"Newtonsoft.Json\" Version=\"2.0.0\"");
    }

    [Fact]
    public void Fix_LiteralVersionOverrideSupersedesEarlierPropertyVersion()
    {
        var projectPath = Path.Combine(_testDirectory, "SupersededPropertyByOverride.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="$(NewtonsoftVersion)" />
                <PackageReference Update="Newtonsoft.Json" VersionOverride="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherSupersededPropertyByOverride.csproj", "Newtonsoft.Json", "2.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (SupersededPropertyByOverride.csproj), 2.0.0 (OtherSupersededPropertyByOverride.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "SupersededPropertyByOverride.csproj"),
                new("Newtonsoft.Json", "2.0.0", otherPath, "OtherSupersededPropertyByOverride.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Version=\"$(NewtonsoftVersion)\"");
        updatedContent.Should().Contain("Update=\"Newtonsoft.Json\" VersionOverride=\"2.0.0\"");
    }

    [Fact]
    public void Fix_PrecedingLiteralVersionOverrideSupersedesLaterPropertyVersion()
    {
        var projectPath = Path.Combine(_testDirectory, "PrecedingOverridePropertyVersion.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Update="Newtonsoft.Json" VersionOverride="1.0.0" />
                <PackageReference Update="Newtonsoft.Json" Version="$(IgnoredVersion)" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherPrecedingOverridePropertyVersion.csproj", "Newtonsoft.Json", "2.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (PrecedingOverridePropertyVersion.csproj), 2.0.0 (OtherPrecedingOverridePropertyVersion.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "PrecedingOverridePropertyVersion.csproj"),
                new("Newtonsoft.Json", "2.0.0", otherPath, "OtherPrecedingOverridePropertyVersion.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("VersionOverride=\"2.0.0\"");
        updatedContent.Should().Contain("Version=\"$(IgnoredVersion)\"");
    }

    [Fact]
    public void Fix_PrecedingIncludeVersionOverrideSupersedesLaterPropertyVersion()
    {
        var projectPath = Path.Combine(_testDirectory, "PrecedingIncludeOverridePropertyVersion.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" VersionOverride="1.0.0" />
                <PackageReference Update="Newtonsoft.Json" Version="$(IgnoredVersion)" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherPrecedingIncludeOverridePropertyVersion.csproj", "Newtonsoft.Json", "2.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (PrecedingIncludeOverridePropertyVersion.csproj), 2.0.0 (OtherPrecedingIncludeOverridePropertyVersion.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "PrecedingIncludeOverridePropertyVersion.csproj"),
                new("Newtonsoft.Json", "2.0.0", otherPath, "OtherPrecedingIncludeOverridePropertyVersion.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("VersionOverride=\"2.0.0\"");
        updatedContent.Should().Contain("Version=\"$(IgnoredVersion)\"");
    }

    [Fact]
    public void Fix_ConditionalOverrideClear_LeavesHiddenOrdinaryVersionUntouched()
    {
        var projectPath = Path.Combine(_testDirectory, "ConditionalOverrideClear.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Newtonsoft.Json" VersionOverride="" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherConditionalOverrideClear.csproj", "Newtonsoft.Json", "3.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "2.0.0 (ConditionalOverrideClear.csproj), 3.0.0 (OtherConditionalOverrideClear.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "2.0.0", projectPath, "ConditionalOverrideClear.csproj"),
                new("Newtonsoft.Json", "3.0.0", otherPath, "OtherConditionalOverrideClear.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Version=\"1.0.0\"");
        updatedContent.Should().Contain("VersionOverride=\"3.0.0\"");
    }

    [Fact]
    public void Fix_ConditionalOverrideClear_LeavesVersionFromEarlierDeclarationUntouched()
    {
        var projectPath = Path.Combine(_testDirectory, "SeparateConditionalOverrideClear.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" />
                <PackageReference Update="Newtonsoft.Json" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Newtonsoft.Json" VersionOverride="" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherSeparateConditionalOverrideClear.csproj", "Newtonsoft.Json", "3.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "2.0.0 (SeparateConditionalOverrideClear.csproj), 3.0.0 (OtherSeparateConditionalOverrideClear.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "2.0.0", projectPath, "SeparateConditionalOverrideClear.csproj"),
                new("Newtonsoft.Json", "3.0.0", otherPath, "OtherSeparateConditionalOverrideClear.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Version=\"1.0.0\"");
        updatedContent.Should().Contain("VersionOverride=\"3.0.0\"");
    }

    [Fact]
    public void Fix_ConditionalOverrideClear_ProtectsLaterUnconditionalVersionUpdate()
    {
        var projectPath = Path.Combine(_testDirectory, "LaterVersionAfterConditionalClear.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Newtonsoft.Json" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Newtonsoft.Json" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherLaterVersionAfterConditionalClear.csproj", "Newtonsoft.Json", "4.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "2.0.0 (LaterVersionAfterConditionalClear.csproj), 4.0.0 (OtherLaterVersionAfterConditionalClear.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "2.0.0", projectPath, "LaterVersionAfterConditionalClear.csproj"),
                new("Newtonsoft.Json", "4.0.0", otherPath, "OtherLaterVersionAfterConditionalClear.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Version=\"1.0.0\"");
        updatedContent.Should().Contain("VersionOverride=\"4.0.0\"");
        updatedContent.Should().Contain("Update=\"Newtonsoft.Json\" Version=\"3.0.0\"");
    }

    [Fact]
    public void Fix_ConditionalOverrideClearBeforeInclude_DoesNotProtectLaterInclude()
    {
        var projectPath = Path.Combine(_testDirectory, "ConditionalClearBeforeInclude.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Update="Newtonsoft.Json" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Newtonsoft.Json" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherConditionalClearBeforeInclude.csproj", "Newtonsoft.Json", "2.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (ConditionalClearBeforeInclude.csproj), 2.0.0 (OtherConditionalClearBeforeInclude.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "ConditionalClearBeforeInclude.csproj"),
                new("Newtonsoft.Json", "2.0.0", otherPath, "OtherConditionalClearBeforeInclude.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Include=\"Newtonsoft.Json\" Version=\"2.0.0\"");
        updatedContent.Should().Contain("Update=\"Newtonsoft.Json\" VersionOverride=\"2.0.0\"");
    }

    [Fact]
    public void Fix_ConditionalOverrideClear_IsScopedToEachIncludeSegment()
    {
        var projectPath = Path.Combine(_testDirectory, "ScopedConditionalClear.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Newtonsoft.Json" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherScopedConditionalClear.csproj", "Newtonsoft.Json", "3.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "2.0.0 (ScopedConditionalClear.csproj), 3.0.0 (OtherScopedConditionalClear.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "2.0.0", projectPath, "ScopedConditionalClear.csproj"),
                new("Newtonsoft.Json", "3.0.0", otherPath, "OtherScopedConditionalClear.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("Include=\"Newtonsoft.Json\" Version=\"1.0.0\" VersionOverride=\"3.0.0\"");
        updatedContent.Should().Contain("Include=\"Newtonsoft.Json\" Version=\"3.0.0\"");
    }

    [Fact]
    public void Fix_EmptyOverrideClearSupersedesEarlierPropertyOverride()
    {
        var projectPath = Path.Combine(_testDirectory, "SupersededPropertyOverride.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" VersionOverride="$(OldOverride)" />
                <PackageReference Update="Newtonsoft.Json" VersionOverride="" />
              </ItemGroup>
            </Project>
            """
        );
        var otherPath = CreateTestProject("OtherSupersededPropertyOverride.csproj", "Newtonsoft.Json", "2.0.0");
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "1.0.0 (SupersededPropertyOverride.csproj), 2.0.0 (OtherSupersededPropertyOverride.csproj)",
            new[] { projectPath, otherPath }
        );
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "1.0.0", projectPath, "SupersededPropertyOverride.csproj"),
                new("Newtonsoft.Json", "2.0.0", otherPath, "OtherSupersededPropertyOverride.csproj"),
            }
        );

        var result = _fixer.Fix(
            issue,
            packageInfo,
            new FixRequest(string.Empty, ConflictStrategy.Highest, DryRun: false)
        );

        result.Success.Should().BeTrue();
        result.Changes.Should().ContainSingle();
        var updatedContent = File.ReadAllText(projectPath);
        updatedContent.Should().Contain("VersionOverride=\"$(OldOverride)\"");
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

    private string CreateUpdateOnlyProject(string projectName, string packageName, string version)
    {
        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Update=""{packageName}"" Version=""{version}"" />
  </ItemGroup>
</Project>";

        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, content);
        return projectPath;
    }

    private string CreateUpdateOnlyVersionOverrideProject(
        string projectName,
        string packageName,
        string version
    )
    {
        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Update=""{packageName}"" VersionOverride=""{version}"" />
  </ItemGroup>
</Project>";

        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, content);
        return projectPath;
    }
}
