using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Analyzers;

public class LiftingAnalyzerTests
{
    private readonly Mock<IDependencyGraphService> _mockGraphService;
    private readonly LiftingAnalyzer _analyzer;

    public LiftingAnalyzerTests()
    {
        _mockGraphService = new Mock<IDependencyGraphService>();
        _analyzer = new LiftingAnalyzer(_mockGraphService.Object);
    }

    [Fact]
    public void Analyze_NoRedundantReferences_ReturnsEmptyIssues()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        _mockGraphService.Setup(s => s.IdentifyRedundantDirectReferences(It.IsAny<string>()))
            .Returns(new List<string>());

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_RedundantLifting_ReturnsIssue()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        _mockGraphService.Setup(s => s.IdentifyRedundantDirectReferences("P1.csproj"))
            .Returns(new List<string> { "Pkg" });

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Pkg");
        result.Issues[0].Description.Should().Contain("provided transitively");
    }
}
