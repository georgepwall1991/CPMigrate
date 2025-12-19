using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Analyzers;

public class VersionInconsistencyAnalyzerTests
{
    private readonly VersionInconsistencyAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_PackagesWithSameVersion_ReturnsNoIssues()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeFalse();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_PackagesWithDifferentVersions_ReturnsIssues()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "12.0.3", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Newtonsoft.Json");
    }

    [Fact]
    public void Analyze_GroupsPackageNamesCaseInsensitively()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("newtonsoft.json", "12.0.3", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
    }
}

public class DuplicatePackageAnalyzerTests
{
    private readonly DuplicatePackageAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_NoDuplicates_ReturnsNoIssues()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Serilog", "3.0.0", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeFalse();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_DifferentCasing_ReturnsIssues()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("newtonsoft.json", "13.0.1", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("casing variations");
    }

    [Fact]
    public void Analyze_ReportsAllCasingVariations()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("newtonsoft.json", "13.0.1", "/path/Project2.csproj", "Project2.csproj"),
            new("NEWTONSOFT.JSON", "13.0.1", "/path/Project3.csproj", "Project3.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("3 casing variations");
    }
}

public class RedundantReferenceAnalyzerTests
{
    private readonly RedundantReferenceAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_NoRedundantReferences_ReturnsNoIssues()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Serilog", "3.0.0", "/path/Project1.csproj", "Project1.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeFalse();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_RedundantReferencesInSameProject_ReturnsIssues()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Newtonsoft.Json");
        result.Issues[0].AffectedProjects.Should().Contain("Project1.csproj");
    }

    [Fact]
    public void Analyze_SamePackageDifferentProjects_ReturnsNoIssues()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "13.0.1", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);

        var result = _analyzer.Analyze(packageInfo);

        result.HasIssues.Should().BeFalse();
    }
}

public class AnalysisServiceTests
{
    [Fact]
    public void Analyze_RunsAllAnalyzers()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        var report = service.Analyze(packageInfo);

        report.Results.Should().HaveCount(3);
        report.ProjectsScanned.Should().Be(1);
        report.TotalPackageReferences.Should().Be(1);
    }

    [Fact]
    public void Analyze_NoIssues_HasIssuesIsFalse()
    {
        var references = new List<PackageReference>
        {
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj")
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
            new("Newtonsoft.Json", "13.0.1", "/path/Project1.csproj", "Project1.csproj"),
            new("Newtonsoft.Json", "12.0.3", "/path/Project2.csproj", "Project2.csproj")
        };
        var packageInfo = new ProjectPackageInfo(references);
        var service = new AnalysisService();

        var report = service.Analyze(packageInfo);

        report.HasIssues.Should().BeTrue();
        report.TotalIssues.Should().BeGreaterThan(0);
    }
}
