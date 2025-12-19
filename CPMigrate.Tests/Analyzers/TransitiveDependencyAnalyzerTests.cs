using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Analyzers;

public class TransitiveDependencyAnalyzerTests
{
    [Fact]
    public void Analyze_TransitiveConflicts_ReturnsIssues()
    {
        // Arrange
        var analyzer = new TransitiveDependencyAnalyzer();
        var references = new List<PackageReference>
        {
            new PackageReference("Newtonsoft.Json", "13.0.1", "Proj1.csproj", "Proj1", IsTransitive: true),
            new PackageReference("Newtonsoft.Json", "12.0.3", "Proj2.csproj", "Proj2", IsTransitive: true),
            new PackageReference("TopLevel", "1.0.0", "Proj1.csproj", "Proj1", IsTransitive: false)
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeTrue();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Newtonsoft.Json");
        result.Issues[0].Description.Should().Contain("12.0.3, 13.0.1");
    }

    [Fact]
    public void Analyze_NoTransitiveConflicts_ReturnsNoIssues()
    {
        // Arrange
        var analyzer = new TransitiveDependencyAnalyzer();
        var references = new List<PackageReference>
        {
            new PackageReference("Newtonsoft.Json", "13.0.1", "Proj1.csproj", "Proj1", IsTransitive: true),
            new PackageReference("Newtonsoft.Json", "13.0.1", "Proj2.csproj", "Proj2", IsTransitive: true)
        };
        var packageInfo = new ProjectPackageInfo(references);

        // Act
        var result = analyzer.Analyze(packageInfo);

        // Assert
        result.HasIssues.Should().BeFalse();
    }
}
