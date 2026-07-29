using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

public class RedundantReferenceAnalyzerTests
{
    private readonly RedundantReferenceAnalyzer _analyzer;

    public RedundantReferenceAnalyzerTests()
    {
        _analyzer = new RedundantReferenceAnalyzer();
    }

    [Fact]
    public void Analyze_NoRedundantReferences_ReturnsEmptyIssues()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg1", "1.0.0", "P1.csproj", "P1.csproj"),
                new PackageReference("Pkg2", "1.0.0", "P1.csproj", "P1.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_RedundantReferences_ReturnsIssue()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1.csproj"),
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Pkg");
        result.Issues[0].AffectedProjects.Should().Contain("P1.csproj");
    }

    [Fact]
    public void Analyze_RedundantReferencesDifferentVersions_ReturnsIssue()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1.csproj"),
                new PackageReference("Pkg", "2.0.0", "P1.csproj", "P1.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("versions: 1.0.0, 2.0.0");
    }
}
