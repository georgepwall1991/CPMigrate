using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

/// <summary>
/// MSBuild resolves <c>Directory.Packages.props</c> from each project's own directory, so a
/// repository can hold several, each governing the projects beneath it.
///
/// <para>
/// Resolving one set for the whole scan and judging every project against it is wrong in both
/// directions: a project governed by a nested props file is measured against pins it never sees
/// (reporting <c>MissingPackageVersion</c>, a High finding, for references that restore perfectly
/// well), and a pin is called orphaned because the projects that use it were reading a different
/// file.
/// </para>
/// </summary>
public class PerProjectCentralPinTests : IDisposable
{
    private readonly string _root;

    public PerProjectCentralPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigratePerProj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
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
    public void Analyze_AProjectGovernedByANestedPropsFile_IsJudgedAgainstIt()
    {
        // Serilog is pinned only in the nested file. Judged against the root file it has no central
        // version at all, which is MissingPackageVersion — a High finding on a project that
        // restores perfectly well.
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"));
        WriteProps("tools", ("Serilog", "4.0.0"));
        var project = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(project);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_APinUsedOnlyByProjectsUnderItsOwnPropsFile_IsNotOrphaned()
    {
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"));
        WriteProps("tools", ("Serilog", "4.0.0"));
        var rootProject = WriteProject("src/Api/Api.csproj", "Newtonsoft.Json");
        var toolProject = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(rootProject, toolProject);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion);
    }

    [Fact]
    public void Analyze_APinNoProjectUnderItsPropsFileUses_IsStillOrphaned()
    {
        // The rule has to keep working per file, or splitting a repository into two props files
        // would silence it everywhere.
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"), ("Unused.Package", "1.0.0"));
        var project = WriteProject("src/Api/Api.csproj", "Newtonsoft.Json");

        var issues = Analyze(project);

        issues
            .Should()
            .Contain(issue =>
                issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion
                && issue.PackageName == "Unused.Package"
            );
    }

    [Fact]
    public void Analyze_APropsFileGoverningNoScannedProject_IsNotJudgedAtAll()
    {
        // The root file governs nothing in this scan — its projects, if any, were not looked at.
        // Reporting its pins as orphaned would make every scoped scan produce a wall of false
        // orphans: `--analyze -s tools` would accuse the whole repository's props file of being
        // dead. Silence is the honest answer when there is no evidence either way.
        WriteProps(".", ("Serilog", "4.0.0"));
        WriteProps("tools", ("Serilog", "4.0.0"));
        var toolProject = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(toolProject);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion);
    }

    [Fact]
    public void Analyze_APropsFileWithCentralManagementOff_IsReportedOnce()
    {
        // Once per file, not once per project beneath it: the misconfiguration is a property of the
        // file, and repeating it turns one problem into a wall.
        File.WriteAllText(
            Path.Combine(_root, CpmDriftAnalyzer.PropsFileName),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var first = WriteProject("src/Api/Api.csproj", "Serilog");
        var second = WriteProject("src/Lib/Lib.csproj", "Serilog");

        var issues = Analyze(first, second);

        issues.Should().ContainSingle(issue => issue.IssueCode == AnalysisIssueCode.CpmNotEnabled);
    }

    [Fact]
    public void Analyze_NoPropsFileAnywhere_ReportsNothing()
    {
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        Analyze(project).Should().BeEmpty();
    }

    private IReadOnlyList<AnalysisIssue> Analyze(params string[] projectPaths)
    {
        var references = projectPaths
            .Select(path => new PackageReference(
                ReadPackageName(path),
                string.Empty,
                path,
                Path.GetFileName(path)
            ))
            .ToList();

        var packageInfo = new ProjectPackageInfo(
            references,
            BasePath: _root,
            ScannedProjects: projectPaths,
            DeclaredReferences: references
        );

        return new CpmDriftAnalyzer().Analyze(packageInfo).Issues;
    }

    private static string ReadPackageName(string projectPath)
    {
        var content = File.ReadAllText(projectPath);
        var marker = "Include=\"";
        var start = content.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return content[start..content.IndexOf('"', start)];
    }

    private void WriteProps(
        string relativeDirectory,
        params (string Package, string Version)[] pins
    )
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);

        var items = string.Join(
            "\n    ",
            pins.Select(pin =>
                $"""<PackageVersion Include="{pin.Package}" Version="{pin.Version}" />"""
            )
        );

        File.WriteAllText(
            Path.Combine(directory, CpmDriftAnalyzer.PropsFileName),
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                {items}
              </ItemGroup>
            </Project>
            """
        );
    }

    private string WriteProject(string relativePath, string package)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{package}" />
              </ItemGroup>
            </Project>
            """
        );

        return path;
    }
}
