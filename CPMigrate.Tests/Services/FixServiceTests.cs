using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for FixService covering fix application workflow,
/// dry-run mode, error handling, and reporting.
/// </summary>
public class FixServiceTests
{
    private readonly FakeConsoleService _console;
    private readonly FixService _fixService;

    public FixServiceTests()
    {
        _console = new FakeConsoleService();
        _fixService = new FixService(_console);
    }

    [Fact]
    public void ApplyFixes_NoIssues_ReturnsSuccessWithNoChanges()
    {
        // Arrange
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 0,
            Results: Array.Empty<AnalyzerResult>()
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.Should().NotBeNull();
        fixReport.TotalFixesApplied.Should().Be(0);
        fixReport.TotalFileChanges.Should().Be(0);
        fixReport.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void ApplyFixes_ReportWithNoIssuesInResults_ReturnsSuccessWithNoChanges()
    {
        // Arrange
        var analyzerResults = new List<AnalyzerResult>
        {
            new("Version Inconsistencies", Array.Empty<AnalysisIssue>())
        };
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 5,
            Results: analyzerResults
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.TotalFixesApplied.Should().Be(0);
        fixReport.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void ApplyFixes_WithVersionInconsistency_InvokesFixers()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var project1Path = Path.Combine(testDir, "Project1.csproj");
            var project2Path = Path.Combine(testDir, "Project2.csproj");

            // Create test projects with same package, different versions
            var content1 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.1"" />
  </ItemGroup>
</Project>";
            var content2 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
            File.WriteAllText(project1Path, content1);
            File.WriteAllText(project2Path, content2);

            var issues = new List<AnalysisIssue>
            {
                new("Newtonsoft.Json",
                    "12.0.1 (Project1), 13.0.1 (Project2)",
                    new[] { project1Path, project2Path },
                    AnalysisIssueCode.VersionInconsistency)
            };
            var analyzerResults = new List<AnalyzerResult>
            {
                new("Version Inconsistencies", issues)
            };
            var report = new AnalysisReport(
                ProjectsScanned: 2,
                TotalPackageReferences: 2,
                Results: analyzerResults
            );

            var packageInfo = new ProjectPackageInfo(new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
                new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj")
            });

            var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

            // Act
            var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

            // Assert
            fixReport.Should().NotBeNull();
            fixReport.Results.Should().HaveCount(1);
            var result = fixReport.Results[0];
            result.Success.Should().BeTrue();
            result.Changes.Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyFixes_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var project1Path = Path.Combine(testDir, "Project1.csproj");
            var originalContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.1"" />
  </ItemGroup>
</Project>";
            File.WriteAllText(project1Path, originalContent);

            var issues = new List<AnalysisIssue>
            {
                new("Newtonsoft.Json",
                    "Version inconsistency",
                    new[] { project1Path },
                    AnalysisIssueCode.VersionInconsistency)
            };
            var analyzerResults = new List<AnalyzerResult>
            {
                new("Version Inconsistencies", issues)
            };
            var report = new AnalysisReport(
                ProjectsScanned: 1,
                TotalPackageReferences: 1,
                Results: analyzerResults
            );

            var packageInfo = new ProjectPackageInfo(new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj")
            });

            var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

            // Act
            var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: true);

            // Assert
            fixReport.Should().NotBeNull();
            // In dry-run, file should not be modified
            File.ReadAllText(project1Path).Should().Be(originalContent);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyFixes_NoFixerAvailable_ContinuesWithoutThrowing()
    {
        // Arrange
        // Create an issue with a description that doesn't match any fixer pattern
        var issues = new List<AnalysisIssue>
        {
            new("UnknownPackage", "Some unknown issue type", new[] { "Project1.csproj" })
        };
        var analyzerResults = new List<AnalyzerResult>
        {
            new("Unknown Analyzer", issues)
        };
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 1,
            Results: analyzerResults
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.Should().NotBeNull();
        fixReport.TotalFixesApplied.Should().Be(0);
        // Service should not throw, just skip unfixable issues
    }

    [Fact]
    public void ApplyFixes_MultipleIssues_ProcessesAll()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"CPMigrateFixServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var project1Path = Path.Combine(testDir, "Project1.csproj");
            var project2Path = Path.Combine(testDir, "Project2.csproj");

            var content1 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.1"" />
    <PackageReference Include=""Serilog"" Version=""2.10.0"" />
  </ItemGroup>
</Project>";
            var content2 = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""2.11.0"" />
  </ItemGroup>
</Project>";
            File.WriteAllText(project1Path, content1);
            File.WriteAllText(project2Path, content2);

            var issues = new List<AnalysisIssue>
            {
                new("Newtonsoft.Json",
                    "12.0.1 (Project1), 13.0.1 (Project2)",
                    new[] { project1Path, project2Path },
                    AnalysisIssueCode.VersionInconsistency),
                new("Serilog",
                    "2.10.0 (Project1), 2.11.0 (Project2)",
                    new[] { project1Path, project2Path },
                    AnalysisIssueCode.VersionInconsistency)
            };
            var analyzerResults = new List<AnalyzerResult>
            {
                new("Version Inconsistencies", issues)
            };
            var report = new AnalysisReport(
                ProjectsScanned: 2,
                TotalPackageReferences: 4,
                Results: analyzerResults
            );

            var packageInfo = new ProjectPackageInfo(new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.1", project1Path, "Project1.csproj"),
                new("Serilog", "2.10.0", project1Path, "Project1.csproj"),
                new("Newtonsoft.Json", "13.0.1", project2Path, "Project2.csproj"),
                new("Serilog", "2.11.0", project2Path, "Project2.csproj")
            });

            var options = new Options { ConflictStrategy = ConflictStrategy.Highest };

            // Act
            var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

            // Assert
            fixReport.Should().NotBeNull();
            fixReport.Results.Should().HaveCount(2); // Two issues fixed
            fixReport.TotalFixesApplied.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyFixes_ReportHasIssuesFalse_ReturnsEarly()
    {
        // Arrange
        var report = new AnalysisReport(
            ProjectsScanned: 1,
            TotalPackageReferences: 0,
            Results: Array.Empty<AnalyzerResult>()
        );
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var options = new Options();

        // Act
        var fixReport = _fixService.ApplyFixes(report, packageInfo, options, dryRun: false);

        // Assert
        fixReport.Should().NotBeNull();
        fixReport.Results.Should().BeEmpty();
        report.HasIssues.Should().BeFalse();
    }
}
