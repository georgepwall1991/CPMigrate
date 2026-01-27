using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

public class FrameworkAlignmentAnalyzerTests
{
    private readonly FrameworkAlignmentAnalyzer _analyzer;

    public FrameworkAlignmentAnalyzerTests()
    {
        _analyzer = new FrameworkAlignmentAnalyzer();
    }

    [Fact]
    public void Analyze_AlignedFrameworks_ReturnsEmptyIssues()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        var projectPath1 = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath1, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>");
        
        var projectPath2 = Path.Combine(tempDir, "P2.csproj");
        File.WriteAllText(projectPath2, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>");

        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", projectPath1, "P1"),
                new PackageReference("Pkg", "1.0.0", projectPath2, "P2")
            },
            new List<VulnerabilityInfo>()
        );

        try
        {
            // Act
            var result = _analyzer.Analyze(packageInfo);

            // Assert
            result.Issues.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Analyze_DivergentFrameworks_ReturnsIssue()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        var projectPath1 = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath1, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>");
        
        var projectPath2 = Path.Combine(tempDir, "P2.csproj");
        File.WriteAllText(projectPath2, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net7.0</TargetFramework></PropertyGroup></Project>");

        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", projectPath1, "P1"),
                new PackageReference("Pkg", "1.0.0", projectPath2, "P2")
            },
            new List<VulnerabilityInfo>()
        );

        try
        {
            // Act
            var result = _analyzer.Analyze(packageInfo);

            // Assert
            result.Issues.Should().HaveCount(1);
            result.Issues[0].Description.Should().Contain("2 different Target Frameworks");
            result.Issues[0].Description.Should().Contain("net6.0");
            result.Issues[0].Description.Should().Contain("net7.0");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
