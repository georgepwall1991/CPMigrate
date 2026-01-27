using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class AnalysisServiceTests
{
    [Fact]
    public void Analyze_RunsAllAnalyzers()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        var report = service.Analyze(packageInfo);

        // We expect some number of analyzers to run
        report.Results.Should().NotBeEmpty();
        report.ProjectsScanned.Should().Be(1);
        report.TotalPackageReferences.Should().Be(1);
    }

    [Fact]
    public void Analyze_NoIssues_HasIssuesIsFalse()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        var report = service.Analyze(packageInfo);

        report.HasIssues.Should().BeFalse();
        report.TotalIssues.Should().Be(0);
    }

    [Fact]
    public void Analyze_WithIssues_HasIssuesIsTrue()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1"),
            new("Newtonsoft.Json", "12.0.3", "/path/Project2.csproj", "Project2")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        var report = service.Analyze(packageInfo);

        report.HasIssues.Should().BeTrue();
        report.TotalIssues.Should().BeGreaterThan(0);
    }
}
