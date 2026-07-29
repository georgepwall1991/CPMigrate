using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

/// <summary>
/// Migrating to central package management is a one-off event; staying migrated is not. Someone adds
/// a package the way they always have — with an inline <c>Version</c> — and the solution is quietly
/// half-centralized again. NuGet does not complain, because an inline version simply wins, so nothing
/// surfaces until two projects disagree and something breaks at runtime.
/// </summary>
public class CpmDriftAnalyzerTests : IDisposable
{
    private readonly string _root;
    private readonly CpmDriftAnalyzer _analyzer = new();

    public CpmDriftAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateDrift_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Analyze_NoPropsFile_ReportsNothing()
    {
        // Every pre-migration repository would otherwise light up with findings about a file it has
        // deliberately not adopted.
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_InlineVersionUnderCpm_IsReported()
    {
        WriteProps("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.IssueCode.Should().Be(AnalysisIssueCode.InlineVersionUnderCpm);
        issue.PackageName.Should().Be("Newtonsoft.Json");
        issue.Description.Should().Contain("12.0.3").And.Contain("13.0.1");
        issue.AffectedProjects.Should().Equal("Api.csproj");
    }

    [Fact]
    public void Analyze_InlineVersionWithNoCentralEntry_IsStillReported()
    {
        WriteProps("<PackageVersion Include=\"Serilog\" Version=\"4.3.0\" />");
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result
            .Issues.Should()
            .Contain(i =>
                i.IssueCode == AnalysisIssueCode.InlineVersionUnderCpm
                && i.PackageName == "Newtonsoft.Json"
            );
    }

    [Fact]
    public void Analyze_CentrallyManagedReference_ReportsNothingForThatPackage()
    {
        WriteProps("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ReferenceWithNoVersionAnywhere_IsReportedAsRestoreBreaking()
    {
        WriteProps("<PackageVersion Include=\"Serilog\" Version=\"4.3.0\" />");
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        var issue = result
            .Issues.Should()
            .ContainSingle(i => i.IssueCode == AnalysisIssueCode.MissingPackageVersion)
            .Subject;
        issue.PackageName.Should().Be("Newtonsoft.Json");
        issue.Severity.Should().Be(AnalysisSeverity.High, "restore fails outright");
    }

    [Fact]
    public void Analyze_OrphanedCentralEntry_IsReported()
    {
        WriteProps(
            "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />",
            "<PackageVersion Include=\"Unused.Package\" Version=\"1.0.0\" />"
        );
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        var issue = result
            .Issues.Should()
            .ContainSingle(i => i.IssueCode == AnalysisIssueCode.OrphanedPackageVersion)
            .Subject;
        issue.PackageName.Should().Be("Unused.Package");
        issue.Severity.Should().Be(AnalysisSeverity.Low, "harmless to restore, but it accumulates");
    }

    [Fact]
    public void Analyze_ProjectContributingNoReferences_IsStillInspected()
    {
        // The fallback scanner skips PackageReference items with no version, so a *correctly*
        // centralized project contributes nothing to References. Deriving the project list from
        // references would therefore skip exactly the projects this analyzer exists to check.
        WriteProps("<PackageVersion Include=\"Serilog\" Version=\"4.3.0\" />");
        var project = WriteProject("Api.csproj", "<PackageReference Include=\"Missing.Entirely\" />");

        var packageInfo = new ProjectPackageInfo(
            Array.Empty<PackageReference>(),
            BasePath: _root,
            ScannedProjects: new[] { project }
        );

        var result = _analyzer.Analyze(packageInfo);

        result
            .Issues.Should()
            .Contain(i =>
                i.IssueCode == AnalysisIssueCode.MissingPackageVersion
                && i.PackageName == "Missing.Entirely"
            );
    }

    [Fact]
    public void Analyze_GlobalPackageReference_IsNeverReportedAsOrphaned()
    {
        // A GlobalPackageReference applies to every project implicitly, so no project-level
        // PackageReference names it — which would make every one of them look unused.
        WriteProps("<GlobalPackageReference Include=\"SonarAnalyzer.CSharp\" Version=\"10.0.0\" />");
        var project = WriteProject("Api.csproj", "<PackageReference Include=\"Serilog\" Version=\"4.3.0\" />");

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result
            .Issues.Should()
            .NotContain(i => i.IssueCode == AnalysisIssueCode.OrphanedPackageVersion);
    }

    [Fact]
    public void Analyze_CpmEnabledInDirectoryBuildProps_IsNotReportedAsDisabled()
    {
        // MSBuild resolves the property through imports, and Directory.Build.props is the other
        // conventional home for it. Reporting on the props file alone is a High-severity false
        // positive on a perfectly well configured repository.
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """
        );
        File.WriteAllText(
            Path.Combine(_root, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        var project = WriteProject("Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" />");

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result.Issues.Should().NotContain(i => i.IssueCode == AnalysisIssueCode.CpmNotEnabled);
    }

    [Fact]
    public void Analyze_CpmDisabled_ReportsOnlyTheConfigurationProblem()
    {
        // With central management off, an inline version overrides nothing — it is simply how every
        // project declares its packages. Continuing would report the whole dependency list as drift.
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result.Issues.Should().ContainSingle().Which.IssueCode.Should().Be(AnalysisIssueCode.CpmNotEnabled);
    }

    [Fact]
    public void Analyze_VersionOverride_IsReportedAsADeliberateDeparture()
    {
        // VersionOverride is NuGet's supported escape hatch, so it is not a mistake the way a stray
        // Version attribute is — but the project has stepped outside the central version, which is
        // exactly what a reviewer needs to see.
        WriteProps("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" VersionOverride=\"12.0.3\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.IssueCode.Should().Be(AnalysisIssueCode.InlineVersionUnderCpm);
        issue.Severity.Should().Be(AnalysisSeverity.Low, "it is deliberate and sanctioned");
        issue.Description.Should().Contain("VersionOverride").And.Contain("Intentional");
    }

    [Fact]
    public void Analyze_TransitivePinningEnabled_DoesNotReportOrphans()
    {
        // With transitive pinning on, a PackageVersion deliberately pins a package no project
        // references directly — so every such pin would otherwise look unused.
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Transitively.Pinned" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Serilog\" Version=\"4.3.0\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result
            .Issues.Should()
            .NotContain(i => i.IssueCode == AnalysisIssueCode.OrphanedPackageVersion);
    }

    [Fact]
    public void Analyze_CentralEntryWithAnEmptyVersion_DoesNotSatisfyAReference()
    {
        // The entry exists but supplies nothing usable, so restore still fails — treating the key's
        // presence as sufficient would hide that.
        WriteProps("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"\" />");
        var project = WriteProject("Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" />");

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result
            .Issues.Should()
            .Contain(i => i.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_GlobalPackageReference_CountsAsACentralVersion()
    {
        // GlobalPackageReference supplies a version centrally too, so a project referencing such a
        // package is not missing anything.
        WriteProps(
            "<GlobalPackageReference Include=\"SonarAnalyzer.CSharp\" Version=\"10.0.0\" />"
        );
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"SonarAnalyzer.CSharp\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result
            .Issues.Should()
            .NotContain(i => i.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_ManagePackageVersionsCentrallyMissing_IsReported()
    {
        // The file looks authoritative and does nothing: NuGet ignores every entry in it.
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """
        );
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result
            .Issues.Should()
            .Contain(i =>
                i.IssueCode == AnalysisIssueCode.CpmNotEnabled
                && i.Severity == AnalysisSeverity.High
            );
    }

    [Fact]
    public void Analyze_ManagePackageVersionsCentrallyFalse_IsReported()
    {
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result
            .Issues.Should()
            .Contain(i =>
                i.IssueCode == AnalysisIssueCode.CpmNotEnabled && i.Description.Contains("'false'")
            );
    }

    [Fact]
    public void Analyze_VersionAsAChildElement_IsRecognisedAsInline()
    {
        // MSBuild accepts both forms; a text search for Version=" would miss this one.
        WriteProps("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        var project = WriteProject(
            "Api.csproj",
            """
            <PackageReference Include="Newtonsoft.Json">
                  <Version>12.0.3</Version>
                </PackageReference>
            """
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result.Issues.Should().Contain(i => i.IssueCode == AnalysisIssueCode.InlineVersionUnderCpm);
    }

    [Fact]
    public void Analyze_EmptyVersionAttribute_IsNotTreatedAsInline()
    {
        // An empty Version does not override a central version, so reporting it would be a false
        // positive on a project that is correctly centralized.
        WriteProps("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_UnparseablePropsFile_IsReportedRatherThanIgnored()
    {
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            "<Project><ItemGroup>"
        );
        var project = WriteProject(
            "Api.csproj",
            "<PackageReference Include=\"Newtonsoft.Json\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(project));

        result.Issues.Should().ContainSingle(i => i.Description.Contains("could not be parsed"));
    }

    [Fact]
    public void Analyze_ProjectsSharingAFileName_AreReportedSeparately()
    {
        WriteProps("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        var source = WriteProject(
            Path.Combine("src", "App", "App.csproj"),
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />"
        );
        var test = WriteProject(
            Path.Combine("tests", "App", "App.csproj"),
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"11.0.2\" />"
        );

        var result = _analyzer.Analyze(PackageInfoFor(source, test));

        result
            .Issues.SelectMany(i => i.AffectedProjects)
            .Should()
            .BeEquivalentTo(new[] { "src/App/App.csproj", "tests/App/App.csproj" });
    }

    private ProjectPackageInfo PackageInfoFor(params string[] projectPaths)
    {
        var references = projectPaths
            .Select(path => new PackageReference(
                "Newtonsoft.Json",
                "13.0.1",
                path,
                Path.GetFileName(path)
            ))
            .ToList();

        return new ProjectPackageInfo(references, BasePath: _root);
    }

    private void WriteProps(params string[] entries)
    {
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                {string.Join("\n    ", entries)}
              </ItemGroup>
            </Project>
            """
        );
    }

    private string WriteProject(string relativePath, string itemXml)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                {itemXml}
              </ItemGroup>
            </Project>
            """
        );

        return fullPath;
    }
}
