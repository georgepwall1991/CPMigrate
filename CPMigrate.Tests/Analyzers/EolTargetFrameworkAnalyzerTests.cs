using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

public class EolTargetFrameworkAnalyzerTests
{
    private readonly EolTargetFrameworkAnalyzer _analyzer;

    public EolTargetFrameworkAnalyzerTests()
    {
        _analyzer = new EolTargetFrameworkAnalyzer();
    }

    [Fact]
    public void Analyze_SingleEolFramework_ReturnsModerateIssue()
    {
        // Arrange
        var projectPath = WriteProject(("P1", "<TargetFramework>netcoreapp3.1</TargetFramework>"));

        var packageInfo = PackageInfo(projectPath);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        var issue = result.Issues[0];
        issue.IssueCode.Should().Be(AnalysisIssueCode.EolTargetFramework);
        issue.Severity.Should().Be(AnalysisSeverity.Moderate);
        issue.Fixable.Should().BeFalse();
        issue.Description.Should().Contain("netcoreapp3.1");
        issue.AffectedProjects.Should().ContainSingle();
    }

    [Fact]
    public void Analyze_SupportedFrameworks_AreSilent()
    {
        foreach (var framework in new[] { "net10.0", "net8.0", "net48", "netstandard2.0", "uap10.0" })
        {
            // Arrange
            var projectPath = WriteProject(
                ("P1", $"<TargetFramework>{framework}</TargetFramework>")
            );

            // Act
            var result = _analyzer.Analyze(PackageInfo(projectPath));

            // Assert
            result.Issues.Should().BeEmpty($"{framework} is not end of life");
        }
    }

    [Fact]
    public void Analyze_MultiTargetWithOneEol_NamesTheEolTargetOnly()
    {
        // Arrange
        var projectPath = WriteProject(
            ("P1", "<TargetFrameworks>net8.0;net6.0</TargetFrameworks>")
        );

        var packageInfo = PackageInfo(projectPath);

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("net6.0");
        result.Issues[0].Description.Should().NotContain("net8.0");
    }

    [Fact]
    public void Analyze_UnreadableFramework_IsSilent()
    {
        // A referenced project whose file does not exist reads as Unknown, and the rule
        // reports nothing rather than guessing.
        var packageInfo = PackageInfo(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csproj"));

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("NETCOREAPP3.1")]
    [InlineData("Net6.0")]
    [InlineData("net7.0-WINDOWS")]
    public void Analyze_TfmMatching_IsCaseInsensitive(string framework)
    {
        // Arrange
        var projectPath = WriteProject(("P1", $"<TargetFramework>{framework}</TargetFramework>"));

        // Act
        var result = _analyzer.Analyze(PackageInfo(projectPath));

        // Assert
        result.Issues.Should().HaveCount(1);
    }

    private static string WriteProject((string Name, string TfmProperty) project)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var projectPath = Path.Combine(tempDir, $"{project.Name}.csproj");
        File.WriteAllText(
            projectPath,
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{project.TfmProperty}"
                + "</PropertyGroup></Project>"
        );
        return projectPath;
    }

    private static ProjectPackageInfo PackageInfo(params string[] projectPaths) =>
        new(
            projectPaths
                .Select(path => new PackageReference("Pkg", "1.0.0", path, "P1"))
                .ToList(),
            new List<VulnerabilityInfo>()
        );
}
