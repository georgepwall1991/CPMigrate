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

    [Fact]
    public void Analyze_Net9_IsEndOfLife()
    {
        // .NET 9 is STS and fell out of support in May 2026.
        var projectPath = WriteProject(("P1", "<TargetFramework>net9.0</TargetFramework>"));

        var result = _analyzer.Analyze(PackageInfo(projectPath));

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("net9.0");
    }

    [Fact]
    public void Analyze_PackageFreeScannedProject_IsStillJudged()
    {
        // A project-level rule must see projects with no packages at all: a fully centralized
        // solution has empty References, and its EOL targets would otherwise pass unexamined.
        var projectPath = WriteProject(("P1", "<TargetFramework>net7.0</TargetFramework>"));
        var packageInfo = new ProjectPackageInfo(
            [],
            new List<VulnerabilityInfo>(),
            ScannedProjects: [projectPath]
        );

        var result = _analyzer.Analyze(packageInfo);

        result.Issues.Should().HaveCount(1);
    }

    [Fact]
    public void Analyze_ReassignedTfm_JudgesEveryDeclarationNotJustTheFirst()
    {
        // The old first-assignment read saw net10.0 and stayed silent; an override to an EOL
        // target further down the file must not hide behind document order.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
                + "<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                + "<PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup>"
                + "</Project>"
        );

        var result = _analyzer.Analyze(PackageInfo(projectPath));

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Description.Should().Contain("net6.0");
    }

    [Theory]
    [InlineData("NETCOREAPP3.1")]
    [InlineData("Net6.0")]
    [InlineData("net9.0")]
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
