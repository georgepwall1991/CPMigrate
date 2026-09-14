using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

/// <summary>
/// A dev-only package without PrivateAssets hands a test framework to every consumer of the
/// library that references it — and nothing anywhere reports that. NuGet resolves the reference
/// cleanly, so the leak only surfaces when someone reads their own dependency list.
/// </summary>
public class DevelopmentDependencyLeakAnalyzerTests : IDisposable
{
    private readonly string _testDirectory;

    public DevelopmentDependencyLeakAnalyzerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateDevLeak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("SonarAnalyzer.CSharp")]
    [InlineData("StyleCop.Analyzers")]
    [InlineData("Meziantou.Analyzer")]
    [InlineData("Microsoft.CodeAnalysis.NetAnalyzers")]
    [InlineData("Microsoft.NET.Test.Sdk")]
    [InlineData("coverlet.collector")]
    [InlineData("coverlet.msbuild")]
    [InlineData("xunit.runner.visualstudio")]
    [InlineData("NUnit3TestAdapter")]
    [InlineData("MSTest.TestAdapter")]
    [InlineData("Microsoft.SourceLink.GitHub")]
    [InlineData("Microsoft.TestPlatform.TestHost")]
    public void Analyze_DevOnlyPackageWithoutPrivateAssets_IsReported(string package)
    {
        var result = Analyze(DeclaredReference(package, "1.0.0"));

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.IssueCode.Should().Be(AnalysisIssueCode.DevelopmentDependencyLeak);
        issue.PackageName.Should().Be(package);
        issue.Fixable.Should().BeTrue();
        issue.Severity.Should().Be(AnalysisSeverity.Low);
    }

    [Theory]
    [InlineData("Newtonsoft.Json")]
    [InlineData("Serilog")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("xunit")]
    [InlineData("xunit.core")]
    [InlineData("Microsoft.Extensions.Logging")]
    public void Analyze_OrdinaryPackage_IsNotReported(string package)
    {
        // Test framework *libraries* are runtime dependencies of the test assembly itself — they
        // belong in the package list however the project is scoped, and flagging them would push
        // PrivateAssets onto references where it changes nothing.
        var result = Analyze(DeclaredReference(package, "13.0.3"));

        result.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("all")]
    [InlineData("All")]
    [InlineData("ALL")]
    [InlineData("*")]
    [InlineData("compile;runtime;all")]
    public void Analyze_DevOnlyPackageWithCoveringPrivateAssets_IsNotReported(string privateAssets)
    {
        var result = Analyze(DeclaredReference("SonarAnalyzer.CSharp", "1.0.0", privateAssets: privateAssets));

        result.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("compile")]
    [InlineData("contentfiles;build")]
    [InlineData("none")]
    public void Analyze_PartialPrivateAssets_IsStillReported(string privateAssets)
    {
        // Scoping some assets still flows the package — it shows up in the consumer's dependency
        // list contributing nothing. Only full coverage suppresses the finding.
        var result = Analyze(DeclaredReference("SonarAnalyzer.CSharp", "1.0.0", privateAssets: privateAssets));

        result.Issues.Should().ContainSingle();
    }

    [Fact]
    public void Analyze_PrivateAssetsViaMetadataOnlyUpdate_CoversTheReference()
    {
        // <PackageReference Update="X" PrivateAssets="all" /> is the supported way to scope an
        // inherited declaration — the Include and the Update are one effective reference.
        var result = Analyze(
            DeclaredReference("SonarAnalyzer.CSharp", "1.0.0"),
            DeclaredReference(
                "SonarAnalyzer.CSharp",
                "",
                privateAssets: "all",
                isMetadataOnlyUpdate: true
            )
        );

        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_SamePackageTwoProjects_ReportsOnlyTheUncoveredOne()
    {
        var result = Analyze(
            DeclaredReference("SonarAnalyzer.CSharp", "1.0.0", "Api.csproj"),
            DeclaredReference("SonarAnalyzer.CSharp", "1.0.0", "Lib.csproj", privateAssets: "all")
        );

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.AffectedProjects.Should().ContainSingle().Which.Should().Be("Api.csproj");
    }

    [Fact]
    public void Analyze_CentralPinWithPrivateAssets_SuppressesProjectFindings()
    {
        // PrivateAssets on the central PackageVersion is the CPM-sanctioned way to scope a dev
        // package once for every project — the projects it governs are already covered.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="SonarAnalyzer.CSharp" Version="1.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """
        );

        var result = Analyze(DeclaredReference("SonarAnalyzer.CSharp", ""));

        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_CentralPinWithoutPrivateAssets_DoesNotSuppressTheProjectFinding()
    {
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="SonarAnalyzer.CSharp" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        Analyze(DeclaredReference("SonarAnalyzer.CSharp", ""))
            .Issues.Should()
            .ContainSingle();
    }

    [Fact]
    public void Analyze_ConditionalCentralPrivateAssets_DoesNotSuppress()
    {
        // A pin that only exists for one configuration cannot scope assets for the others — the
        // package still flows to consumers of every configuration it does not cover.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageVersion Include="SonarAnalyzer.CSharp" Version="1.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """
        );

        Analyze(DeclaredReference("SonarAnalyzer.CSharp", ""))
            .Issues.Should()
            .ContainSingle();
    }

    [Fact]
    public void Analyze_GlobalPackageReferenceWithoutPrivateAssets_IsReportedAgainstThePropsFile()
    {
        // A global reference injects the package into every project — the leak is in the props
        // file, and the finding names it rather than any one project.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <GlobalPackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var issue = Analyze().Issues.Should().ContainSingle().Subject;
        issue.PackageName.Should().Be("Microsoft.SourceLink.GitHub");
        issue.IssueCode.Should().Be(AnalysisIssueCode.DevelopmentDependencyLeak);
        issue.AffectedProjects.Should().BeEmpty();
        issue.Metadata!["propsFile"].Should().Be("Directory.Packages.props");
    }

    [Fact]
    public void Analyze_GlobalPackageReferenceWithPrivateAssets_IsNotReported()
    {
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <GlobalPackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """
        );

        Analyze().Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_NullDeclaredReferences_ReportsOnlyGlobalLeaks()
    {
        // A null declared list means the declarations could not be read — "could not look" must
        // not report the same findings as "looked and they were missing". Central findings are
        // still real: the props file was read directly.
        var packageInfo = new ProjectPackageInfo(
            References: new[] { Reference("SonarAnalyzer.CSharp", "1.0.0") },
            BasePath: _testDirectory,
            DeclaredReferences: null
        );

        new DevelopmentDependencyLeakAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_TransitiveDevOnlyPackage_IsNotReported()
    {
        // The declaration a consumer's project file never wrote is not theirs to scope — the leak
        // belongs to whichever direct reference brought it in.
        var transitive = Reference("SonarAnalyzer.CSharp", "1.0.0") with { IsTransitive = true };

        Analyze(transitive).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ScannerReadsPrivateAssetsFromTheProjectFile()
    {
        // The rule reads what the scanner captured; this proves the parse end of the pipeline
        // against a real project file rather than a constructed reference.
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="1.0.0" />
                <PackageReference Include="coverlet.collector" Version="6.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declared, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declared.Should().ContainSingle(reference =>
            reference.PackageName == "SonarAnalyzer.CSharp" && reference.PrivateAssets == null
        );
        declared.Should().ContainSingle(reference =>
            reference.PackageName == "coverlet.collector" && reference.PrivateAssets == "all"
        );

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declared
        );

        var issue = new DevelopmentDependencyLeakAnalyzer()
            .Analyze(packageInfo)
            .Issues.Should()
            .ContainSingle()
            .Subject;
        issue.PackageName.Should().Be("SonarAnalyzer.CSharp");
    }

    [Fact]
    public void Analyze_ConditionalPrivateAssetsMetadata_StillReports()
    {
        // PrivateAssets scoped to one target framework leaves the package flowing on every other —
        // coverage has to hold unconditionally.
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="SonarAnalyzer.CSharp" Version="1.0.0">
                  <PrivateAssets Condition="'$(TargetFramework)' == 'net8.0'">all</PrivateAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declared, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declared.Should().ContainSingle().Which.PrivateAssets.Should().BeNull();

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declared
        );

        new DevelopmentDependencyLeakAnalyzer()
            .Analyze(packageInfo)
            .Issues.Should()
            .ContainSingle();
    }

    private AnalyzerResult Analyze(params PackageReference[] declared)
    {
        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declared
        );

        return new DevelopmentDependencyLeakAnalyzer().Analyze(packageInfo);
    }

    private PackageReference DeclaredReference(
        string package,
        string version,
        string projectFile = "App.csproj",
        string? privateAssets = null,
        bool isMetadataOnlyUpdate = false
    ) =>
        Reference(package, version, projectFile) with
        {
            PrivateAssets = privateAssets,
            IsMetadataOnlyUpdate = isMetadataOnlyUpdate,
        };

    private PackageReference Reference(
        string package,
        string version,
        string projectFile = "App.csproj"
    ) => new(package, version, Path.Combine(_testDirectory, projectFile), projectFile);

    private void WriteProps(string content) =>
        File.WriteAllText(Path.Combine(_testDirectory, CpmDriftAnalyzer.PropsFileName), content);
}
