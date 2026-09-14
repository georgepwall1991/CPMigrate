using CPMigrate.Fixers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// The <c>DevelopmentDependencyLeak</c> fixer sets <c>PrivateAssets="all"</c> on references to
/// dev-only packages — the edit the finding's own hint names. Existing coverage is left alone;
/// partial coverage is replaced, because some-assets-private is still a transitive package.
/// </summary>
public class PrivateAssetsFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly PrivateAssetsFixer _fixer;

    public PrivateAssetsFixerTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigratePrivateAssetsFixer_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_testDirectory);
        _fixer = new PrivateAssetsFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_DevelopmentDependencyLeak_ReturnsTrue()
    {
        _fixer.CanFix(Issue("SonarAnalyzer.CSharp")).Should().BeTrue();
    }

    [Fact]
    public void CanFix_OtherIssueCode_ReturnsFalse()
    {
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Some other issue",
            new[] { "App.csproj" }
        );

        _fixer.CanFix(issue).Should().BeFalse();
    }

    [Fact]
    public void Fix_UnscopedReference_AddsPrivateAssetsAttribute()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("SonarAnalyzer.CSharp"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        File.ReadAllText(projectPath).Should().Contain("PrivateAssets=\"all\"");
    }

    [Fact]
    public void Fix_PartialPrivateAssets_IsReplacedWithAll()
    {
        // Partial scoping still lets the package flow — the fix is full coverage, not a merge.
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="10.0.0" PrivateAssets="compile" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("SonarAnalyzer.CSharp"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("PrivateAssets=\"all\"");
        content.Should().NotContain("PrivateAssets=\"compile\"");
    }

    [Fact]
    public void Fix_AlreadyCoveredReference_IsUntouched()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="10.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(projectPath);

        var result = _fixer.Fix(Issue("SonarAnalyzer.CSharp"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().BeEmpty();
        File.ReadAllText(projectPath).Should().Be(before);
    }

    [Fact]
    public void Fix_ChildElementForm_UpdatesTheElement()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="10.0.0">
                  <PrivateAssets>compile</PrivateAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("SonarAnalyzer.CSharp"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        File.ReadAllText(projectPath).Should().Contain("<PrivateAssets>all</PrivateAssets>");
    }

    [Fact]
    public void Fix_ConditionedPrivateAssetsElement_IsRewrittenUnconditional()
    {
        // Coverage that only holds on one configuration still leaks on the others — the fix
        // removes the condition rather than leaving a scoping that is partial by configuration.
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="10.0.0">
                  <PrivateAssets Condition="'$(TargetFramework)' == 'net8.0'">all</PrivateAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("SonarAnalyzer.CSharp"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        File.ReadAllText(projectPath).Should().Contain("<PrivateAssets>all</PrivateAssets>");
    }

    [Fact]
    public void Fix_DryRun_ReportsChangeButDoesNotWrite()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(projectPath);

        var result = _fixer.Fix(Issue("SonarAnalyzer.CSharp"), PackageInfo(projectPath), Request(dryRun: true));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        File.ReadAllText(projectPath).Should().Be(before);
    }

    [Fact]
    public void Fix_OtherPackageUntouched()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="10.0.0" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("SonarAnalyzer.CSharp"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("PrivateAssets=\"all\"");
        content.Should().Contain("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\"");
    }

    [Fact]
    public void Fix_GlobalReferenceIssue_EditsThePropsFile()
    {
        // The global finding names the props file in metadata rather than a project — the fix
        // scopes the GlobalPackageReference item there, not any project file.
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(
            propsPath,
            """
            <Project>
              <ItemGroup>
                <GlobalPackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var issue = new AnalysisIssue(
            "Microsoft.SourceLink.GitHub",
            "Global reference without PrivateAssets",
            Array.Empty<string>(),
            AnalysisIssueCode.DevelopmentDependencyLeak,
            AnalysisSeverity.Low,
            Fixable: true,
            Metadata: new Dictionary<string, string> { ["propsFile"] = "Directory.Packages.props" }
        );

        var result = _fixer.Fix(issue, PackageInfo(projectPath: null), Request(dryRun: false));

        result.Success.Should().BeTrue();
        File.ReadAllText(propsPath).Should().Contain("PrivateAssets=\"all\"");
    }

    private string WriteProject(string content)
    {
        var path = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    private static AnalysisIssue Issue(string packageName) =>
        new(
            packageName,
            "Dev-only package referenced without PrivateAssets",
            new[] { "App.csproj" },
            AnalysisIssueCode.DevelopmentDependencyLeak,
            AnalysisSeverity.Low,
            Fixable: true
        );

    private ProjectPackageInfo PackageInfo(string? projectPath) =>
        new(
            projectPath is null
                ? new List<PackageReference>()
                : new List<PackageReference>
                {
                    new("SonarAnalyzer.CSharp", "10.0.0", projectPath, "App.csproj"),
                },
            BasePath: _testDirectory
        );

    private static FixRequest Request(bool dryRun) =>
        new("Directory.Packages.props", ConflictStrategy.Highest, dryRun);
}
