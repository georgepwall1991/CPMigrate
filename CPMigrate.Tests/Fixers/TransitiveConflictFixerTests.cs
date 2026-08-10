using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// Tests for TransitiveConflictFixer covering version pinning,
/// props file updates, and edge cases.
/// </summary>
public class TransitiveConflictFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly TransitiveConflictFixer _fixer;
    private readonly FakeConsoleService _console;

    public TransitiveConflictFixerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateTransitiveFixerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        var versionResolver = new VersionResolver(_console);
        _fixer = new TransitiveConflictFixer(versionResolver);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_TransitiveDependencyDescription_ReturnsTrue()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict detected",
            Array.Empty<string>(),
            AnalysisIssueCode.TransitiveConflict
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
            "Version inconsistency detected",
            Array.Empty<string>()
        );

        // Act
        var canFix = _fixer.CanFix(issue);

        // Assert
        canFix.Should().BeFalse();
    }

    [Fact]
    public void Fix_PropsFileDoesNotExist_ReturnsFailed()
    {
        // Arrange
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeFalse();
        result.Description.Should().Contain("Directory.Packages.props not found");
    }

    [Fact]
    public void Fix_NoVersionsFound_ReturnsFailed()
    {
        // Arrange
        CreatePropsFile();

        var issue = new AnalysisIssue(
            "NonExistentPackage",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeFalse();
        result.Description.Should().Contain("Could not determine versions");
    }

    [Fact]
    public void Fix_PackageNotInProps_AddsNewEntry()
    {
        // Arrange
        CreatePropsFile();

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("Pinned Newtonsoft.Json to version 13.0.1");
        result.Changes.Should().HaveCount(1);

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("Include=\"Newtonsoft.Json\"");
        content.Should().Contain("Version=\"13.0.1\"");
    }

    [Fact]
    public void Fix_PackageExistsWithInclude_UpdatesVersion()
    {
        // Arrange
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""12.0.1"" />
    <PackageVersion Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("Pinned Newtonsoft.Json to version 13.0.1");

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("Newtonsoft.Json");
        content.Should().Contain("13.0.1");
        content.Should().NotContain("12.0.1");
        // Serilog should be unchanged
        content.Should().Contain("Serilog");
        content.Should().Contain("3.0.0");
    }

    [Fact]
    public void Fix_PackageExistsWithChildVersion_UpdatesVersion()
    {
        // MSBuild accepts Version metadata as a child element, and this repository uses that form
        // for several central pins. Leaving it unchanged makes a successful-looking fix leave the
        // transitive conflict in place.
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"">
      <Version>12.0.1</Version>
    </PackageVersion>
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("Pinned Newtonsoft.Json to version 13.0.1");

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("<Version>13.0.1</Version>");
        content.Should().NotContain("<Version>12.0.1</Version>");
    }

    [Fact]
    public void Fix_AttributeVersionTarget_DoesNotRewriteChildVersionNeighbor()
    {
        // A mixed-style props file is valid MSBuild. The child-version matcher must stay within
        // the target PackageVersion instead of carrying the target package into a later entry.
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""12.0.1"" />
    <PackageVersion Include=""Serilog"">
      <Version>3.0.0</Version>
    </PackageVersion>
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        var content = File.ReadAllText(Path.Combine(_testDirectory, "Directory.Packages.props"));
        content.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        content.Should().Contain("<Version>3.0.0</Version>");
        content.Should().NotContain("<Version>13.0.1</Version>");
    }

    [Fact]
    public void Fix_ConditionalChildVersion_LeavesPinUnchanged()
    {
        // A conditional central pin may be valid for only one framework/configuration. Without
        // evaluating MSBuild conditions, changing it would make the other configurations unsafe.
        CreatePropsFile(@"<Project>
  <ItemGroup Condition=""'$(TargetFramework)' == 'net8.0'"">
    <PackageVersion Include=""Newtonsoft.Json""><Version>12.0.1</Version></PackageVersion>
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeFalse();
        result.Description.Should().Contain("conditional");
        var content = File.ReadAllText(Path.Combine(_testDirectory, "Directory.Packages.props"));
        content.Should().Contain("<Version>12.0.1</Version>");
        content.Should().NotContain("<Version>13.0.1</Version>");
    }

    [Fact]
    public void Fix_CommentedChildVersion_AddsActivePinWithoutChangingComment()
    {
        // Commented examples are not active central pins. They must not satisfy the existing-pin
        // branch or be rewritten as if they controlled restore.
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <!-- <PackageVersion Include=""Newtonsoft.Json""><Version>12.0.1</Version></PackageVersion> -->
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        var content = File.ReadAllText(Path.Combine(_testDirectory, "Directory.Packages.props"));
        content.Should().Contain("<!-- <PackageVersion Include=\"Newtonsoft.Json\"><Version>12.0.1</Version></PackageVersion> -->");
        content.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
    }

    [Fact]
    public void Fix_PackageExistsWithUpdate_UpdatesVersion()
    {
        // Arrange
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <PackageVersion Update=""Newtonsoft.Json"" Version=""12.0.1"" />
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("Pinned Newtonsoft.Json to version 13.0.1");

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("Update=\"Newtonsoft.Json\"");
        content.Should().Contain("13.0.1");
        content.Should().NotContain("12.0.1");
    }

    [Fact]
    public void Fix_HighestStrategy_SelectsHighestVersion()
    {
        // Arrange
        CreatePropsFile();

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj"),
            new("Newtonsoft.Json", "12.0.5", "Project3.csproj", "Project3.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("Version=\"13.0.1\"");
    }

    [Fact]
    public void Fix_LowestStrategy_SelectsLowestVersion()
    {
        // Arrange
        CreatePropsFile();

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "12.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "Project2.csproj", "Project2.csproj"),
            new("Newtonsoft.Json", "12.0.5", "Project3.csproj", "Project3.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Lowest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("Version=\"12.0.1\"");
    }

    [Fact]
    public void Fix_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""12.0.1"" />
  </ItemGroup>
</Project>");

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var originalContent = File.ReadAllText(propsPath);

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);

        // File should not be modified in dry-run
        File.ReadAllText(propsPath).Should().Be(originalContent);
        originalContent.Should().Contain("12.0.1");
    }

    [Fact]
    public void Fix_PackageAlreadyAtCorrectVersion_ReturnsNoFixNeeded()
    {
        // Arrange
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Description.Should().Contain("already aligned");
    }

    [Fact]
    public void Fix_CaseInsensitivePackageMatching_UpdatesCorrectly()
    {
        // Arrange
        CreatePropsFile(@"<Project>
  <ItemGroup>
    <PackageVersion Include=""newtonsoft.json"" Version=""12.0.1"" />
  </ItemGroup>
</Project>");

        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Transitive dependency conflict",
            Array.Empty<string>()
        );

        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1.csproj")
        });

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            ConflictStrategy = ConflictStrategy.Highest
        };

        // Act
        var result = _fixer.Fix(issue, packageInfo, options, dryRun: false);

        // Assert
        result.Success.Should().BeTrue();

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("Version=\"13.0.1\"");
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        // Assert
        _fixer.Name.Should().Be("Transitive Conflict Pinning");
    }

    // Helper methods

    private void CreatePropsFile(string? content = null)
    {
        content ??= @"<Project>
  <ItemGroup>
  </ItemGroup>
</Project>";

        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, content);
    }
}
