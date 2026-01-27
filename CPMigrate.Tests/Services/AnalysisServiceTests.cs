using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for AnalysisService covering analyzer orchestration and edge cases.
/// </summary>
public class AnalysisServiceTests
{
    [Fact]
    public void Constructor_NoParameters_CreatesDefaultAnalyzers()
    {
        // Act
        var service = new AnalysisService();

        // Assert - Analyze should work without throwing
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var report = service.Analyze(packageInfo);

        report.Should().NotBeNull();
        report.Results.Should().NotBeEmpty(); // Should have default analyzers
    }

    [Fact]
    public void Constructor_CustomAnalyzers_UsesProvidedAnalyzers()
    {
        // Arrange
        var customAnalyzers = new List<IAnalyzer>
        {
            new VersionInconsistencyAnalyzer()
        };

        // Act
        var service = new AnalysisService(customAnalyzers);
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());
        var report = service.Analyze(packageInfo);

        // Assert
        report.Should().NotBeNull();
        report.Results.Should().HaveCount(1); // Only the custom analyzer
    }

    [Fact]
    public void Analyze_EmptyPackageInfo_ReturnsEmptyReport()
    {
        // Arrange
        var service = new AnalysisService();
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.Should().NotBeNull();
        report.ProjectsScanned.Should().Be(0);
        report.TotalPackageReferences.Should().Be(0);
        report.Results.Should().NotBeEmpty(); // Has analyzer results (even if empty)
    }

    [Fact]
    public void Analyze_WithPackageReferences_RunsAllAnalyzers()
    {
        // Arrange
        var service = new AnalysisService();
        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "12.0.1", "Project2.csproj", "Project2.csproj")
        });

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.Should().NotBeNull();
        report.ProjectsScanned.Should().Be(2);
        report.TotalPackageReferences.Should().Be(2);
        report.Results.Should().NotBeEmpty();

        // Should detect version inconsistency
        report.HasIssues.Should().BeTrue();
        report.Results.Should().Contain(r => r.AnalyzerName == "Version Inconsistencies");
    }

    [Fact]
    public void Analyze_WithDuplicateReferences_DetectsIssues()
    {
        // Arrange
        var service = new AnalysisService();
        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1"),
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1"), // Duplicate
            new("newtonsoft.json", "13.0.1", "Project2.csproj", "Project2") // Casing variation
        });

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.Should().NotBeNull();
        report.HasIssues.Should().BeTrue();

        // Should detect both redundant references and casing variations
        var duplicateResult = report.Results.FirstOrDefault(r => r.AnalyzerName == "Duplicate Packages");
        var redundantResult = report.Results.FirstOrDefault(r => r.AnalyzerName == "Redundant References");

        (duplicateResult?.Issues.Count > 0 || redundantResult?.Issues.Count > 0).Should().BeTrue();
    }

    [Fact]
    public void Analyze_WithConsistentReferences_HasNoIssues()
    {
        // Arrange
        var service = new AnalysisService();
        var packageInfo = new ProjectPackageInfo(new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "Project1.csproj", "Project1.csproj"),
            new("Serilog", "3.0.0", "Project2.csproj", "Project2.csproj")
        });

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.Should().NotBeNull();

        // No version inconsistencies, duplicates, or redundant references
        var versionResult = report.Results.FirstOrDefault(r => r.AnalyzerName == "Version Inconsistencies");
        var duplicateResult = report.Results.FirstOrDefault(r => r.AnalyzerName == "Duplicate Packages");
        var redundantResult = report.Results.FirstOrDefault(r => r.AnalyzerName == "Redundant References");

        versionResult?.Issues.Should().BeEmpty();
        duplicateResult?.Issues.Should().BeEmpty();
        redundantResult?.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ReportIncludesAllAnalyzerResults()
    {
        // Arrange
        var service = new AnalysisService();
        var packageInfo = new ProjectPackageInfo(Array.Empty<PackageReference>());

        // Act
        var report = service.Analyze(packageInfo);

        // Assert
        report.Results.Should().NotBeEmpty();

        // Should have results from multiple default analyzers
        report.Results.Count.Should().BeGreaterThan(3);

        // All results should have an analyzer name
        report.Results.Should().AllSatisfy(r => r.AnalyzerName.Should().NotBeNullOrEmpty());
    }
}
