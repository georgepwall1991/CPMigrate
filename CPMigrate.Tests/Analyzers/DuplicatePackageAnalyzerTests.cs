using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

public class DuplicatePackageAnalyzerTests
{
    private readonly DuplicatePackageAnalyzer _analyzer;

    public DuplicatePackageAnalyzerTests()
    {
        _analyzer = new DuplicatePackageAnalyzer();
    }

    [Fact]
    public void Analyze_NoDuplicates_ReturnsEmptyIssues()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Newtonsoft.Json", "13.0.1", "P1.csproj", "P1.csproj"),
                new PackageReference("Microsoft.Extensions.DependencyInjection", "6.0.0", "P2.csproj", "P2.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_CasingDuplicates_ReturnsIssue()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Newtonsoft.Json", "13.0.1", "P1.csproj", "P1.csproj"),
                new PackageReference("newtonsoft.json", "13.0.1", "P2.csproj", "P2.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Newtonsoft.Json");
        result.Issues[0].Description.Should().Contain("2 casing variations");
        result.Issues[0].Description.Should().Contain("Newtonsoft.Json");
        result.Issues[0].Description.Should().Contain("newtonsoft.json");
        result.Issues[0].AffectedProjects.Should().Contain(new[] { "P1.csproj", "P2.csproj" });
    }

    [Fact]
    public void Analyze_MultipleCasingVariations_ReturnsIssue()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Newtonsoft.Json", "13.0.1", "P1.csproj", "P1.csproj"),
                new PackageReference("newtonsoft.json", "13.0.1", "P2.csproj", "P2.csproj"),
                // Actually the analyzer groups by lowercase, so "NEWTONSOFT.JSON" is another variation
                new PackageReference("NEWTONSOFT.JSON", "13.0.1", "P3.csproj", "P3.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("3 casing variations");
    }
}
