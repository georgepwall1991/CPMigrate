using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

public class VersionInconsistencyAnalyzerTests
{
    private readonly VersionInconsistencyAnalyzer _analyzer;

    public VersionInconsistencyAnalyzerTests()
    {
        _analyzer = new VersionInconsistencyAnalyzer();
    }

    [Fact]
    public void Analyze_ConsistentVersions_ReturnsEmptyIssues()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1"),
                new PackageReference("Pkg", "1.0.0", "P2.csproj", "P2")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_InconsistentVersions_ReturnsIssue()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1"),
                new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Pkg");
        result.Issues[0].Description.Should().Contain("1.0.0 (P1)");
        result.Issues[0].Description.Should().Contain("2.0.0 (P2)");
        result.Issues[0].AffectedProjects.Should().Contain(new[] { "P1", "P2" });
    }
}
