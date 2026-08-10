using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

/// <summary>
/// A floating version is the difference between a build that is reproducible and one that is not:
/// the same commit restores a different package tomorrow, so a green CI run says nothing about what
/// will ship. NuGet reports none of this — a wildcard resolving to a new major is a successful
/// restore.
/// </summary>
public class FloatingVersionAnalyzerTests : IDisposable
{
    private readonly string _testDirectory;

    public FloatingVersionAnalyzerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateFloat_{Guid.NewGuid():N}");
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
    [InlineData("*")]
    [InlineData("1.*")]
    [InlineData("13.0.*")]
    [InlineData("1.2.3-*")]
    [InlineData("[1.0.0,2.0.0)")]
    [InlineData("(1.0.0,)")]
    [InlineData("[1.0.0,)")]
    [InlineData("[1.0.0, )")]
    public void Analyze_NonExactVersionInAProjectFile_IsReported(string version)
    {
        var result = Analyze(DeclaredReference("Newtonsoft.Json", version));

        result
            .Issues.Should()
            .ContainSingle()
            .Which.IssueCode.Should()
            .Be(AnalysisIssueCode.FloatingVersion);
    }

    [Theory]
    [InlineData("13.0.1")]
    [InlineData("13.0.1-beta1")]
    [InlineData("[13.0.1]")]
    [InlineData("[13.0.1] ")]
    [InlineData("[13.0.1,13.0.1]")]
    [InlineData("13.0.1+build.5")]
    public void Analyze_ExactVersion_IsNotReported(string version)
    {
        // [13.0.1] is the *most* exact form NuGet has — a bracketed single version locks the
        // package to it. Reporting it would flag the fix as the defect. [13.0.1,13.0.1] is a range
        // whose bounds coincide, so exactly one version satisfies it and it is every bit as
        // reproducible: the question is what a specification can resolve to, not how it is spelled.
        var result = Analyze(DeclaredReference("Newtonsoft.Json", version));

        result.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Analyze_NoVersionAtAll_IsLeftToTheRuleThatOwnsIt(string version)
    {
        // A reference with no version is either centrally managed or a restore failure waiting to
        // happen; MissingPackageVersion says which. Reporting it here would double-count it.
        var result = Analyze(DeclaredReference("Newtonsoft.Json", version));

        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ReadsDeclaredReferencesRatherThanResolvedOnes()
    {
        // The resolved graph is what `dotnet package list` reports, and resolution has already
        // turned "1.*" into a concrete version — so a rule reading it can never see a float. This
        // is the shape that made three earlier rules incapable of firing.
        var packageInfo = new ProjectPackageInfo(
            References: new[] { Reference("Newtonsoft.Json", "13.0.3") },
            BasePath: _testDirectory,
            DeclaredReferences: new[] { Reference("Newtonsoft.Json", "1.*") }
        );

        var result = new FloatingVersionAnalyzer().Analyze(packageInfo);

        result.Issues.Should().ContainSingle();
    }

    [Fact]
    public void Analyze_CentralPackageVersion_IsReported()
    {
        // After a migration every version lives in the props file, so a rule that only read project
        // files would go quiet on exactly the solutions this tool exists to produce.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.*" />
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """
        );

        var result = Analyze();

        result.Issues.Should().ContainSingle().Which.PackageName.Should().Be("Serilog");
    }

    [Fact]
    public void Analyze_CentralPinInChildElementForm_IsReported()
    {
        // MSBuild accepts metadata as a child element, and this repository's own props file uses
        // that form. A rule with its own simpler parser reads no version here and reports the
        // solution clean — which is indistinguishable from a solution with nothing wrong.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog">
                  <Version>4.*</Version>
                </PackageVersion>
              </ItemGroup>
            </Project>
            """
        );

        Analyze().Issues.Should().ContainSingle().Which.PackageName.Should().Be("Serilog");
    }

    [Fact]
    public void Analyze_CentralPinUsingTheUpdateForm_IsReported()
    {
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Update="Serilog" Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );

        Analyze().Issues.Should().ContainSingle().Which.PackageName.Should().Be("Serilog");
    }

    [Fact]
    public void Analyze_CentralPinInAnImportedPropsFile_IsReported()
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, "Versions.props"),
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <Import Project="Versions.props" />
            </Project>
            """
        );

        Analyze().Issues.Should().ContainSingle().Which.PackageName.Should().Be("Serilog");
    }

    [Fact]
    public void Analyze_CentralPinInAConditionalImportedPropsFile_IsReportedAsFloating()
    {
        // The import may not apply in every configuration, so drift must not use its pin as
        // universal evidence. The declaration is still real in the configurations where the
        // import runs and must remain visible to the floating-version rule.
        File.WriteAllText(
            Path.Combine(_testDirectory, "Release.Packages.props"),
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <Import Project="Release.Packages.props" Condition="'$(Configuration)' == 'Release'" />
            </Project>
            """
        );

        Analyze().Issues.Should().ContainSingle().Which.PackageName.Should().Be("Serilog");
    }

    [Fact]
    public void Analyze_ConditionalCentralPin_IsReportedAsFloating()
    {
        // Drift completeness must stand down for a configuration-specific pin, but the pin can
        // still float whenever that configuration applies and must remain visible to this rule.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageVersion Include="Serilog" Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );

        Analyze().Issues.Should().ContainSingle().Which.PackageName.Should().Be("Serilog");
    }

    [Fact]
    public void Analyze_CentralPinWithCentralManagementSwitchedOff_IsNotReported()
    {
        // NuGet ignores every PackageVersion in a props file that does not enable central
        // management, so reporting one would be a finding about a value nothing consults — and it
        // would fail the default gate for a project pinning exactly, inline.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );

        Analyze().Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_GlobalPackageReference_IsReported()
    {
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <GlobalPackageReference Include="Microsoft.SourceLink.GitHub" Version="[8.0.0,)" />
              </ItemGroup>
            </Project>
            """
        );

        var result = Analyze();

        result
            .Issues.Should()
            .ContainSingle()
            .Which.PackageName.Should()
            .Be("Microsoft.SourceLink.GitHub");
    }

    [Fact]
    public void Analyze_ReportsTheVersionAndTheKindSoTheFindingIsActionable()
    {
        var result = Analyze(DeclaredReference("Serilog", "4.*"));

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Description.Should().Contain("4.*");
        issue.Metadata.Should().ContainKey("versionSpecification");
        issue.Metadata!["versionSpecification"].Should().Be("4.*");
        issue.Metadata.Should().ContainKey("kind");
        issue.Metadata["kind"].Should().Be("wildcard");
    }

    [Fact]
    public void Analyze_ARangeIsReportedAsARangeRatherThanAWildcard()
    {
        var result = Analyze(DeclaredReference("Serilog", "[4.0.0,5.0.0)"));

        result.Issues.Should().ContainSingle().Which.Metadata!["kind"].Should().Be("range");
    }

    [Fact]
    public void Analyze_OnePackageFloatingInSeveralProjects_IsOneFinding()
    {
        // One decision, one finding: a package listed once per project turns a single mistake into
        // a wall of duplicates, and the affected projects already say where it is.
        var result = Analyze(
            DeclaredReference("Serilog", "4.*", "Api.csproj"),
            DeclaredReference("Serilog", "4.*", "Lib.csproj")
        );

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.AffectedProjects.Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_TheSamePackageFloatingTwoDifferentWays_IsReportedSeparately()
    {
        // Two different specifications are two different decisions, and collapsing them would hide
        // one of the two strings someone has to go and change.
        var result = Analyze(
            DeclaredReference("Serilog", "4.*", "Api.csproj"),
            DeclaredReference("Serilog", "[4.0.0,)", "Lib.csproj")
        );

        result.Issues.Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_ConditionalUpdatesUnderSameCondition_DoNotReportSupersededFloat()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="4.*" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle().Which.Version.Should().Be("4.0.0");

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ConditionalIncludeAndUpdateUnderSameCondition_DoNotReportSupersededFloat()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.*" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle().Which.Version.Should().Be("4.0.0");

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_CombinedMetadataGuards_DoNotPreserveSupersededFloat()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(Configuration)' == 'Debug' And '$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">4.*</VersionOverride>
                </PackageReference>
                <PackageReference Update="Serilog" VersionOverride="4.0.0" Condition="'$(TargetFramework)' == 'net8.0'" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_NestedPropertyFunctionMutationDoesNotFoldIdenticalGuards()
    {
        var projectPath = Path.Combine(_testDirectory, "NestedPropertyFunctionMutation.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <PropertyGroup>
                <Mode>A</Mode>
              </PropertyGroup>
              <ItemGroup Condition="$([System.String]::Equals('$(Mode)', 'A'))">
                <PackageReference Include="Serilog" Version="4.*" />
              </ItemGroup>
              <PropertyGroup>
                <Mode>B</Mode>
              </PropertyGroup>
              <ItemGroup Condition="$([System.String]::Equals('$(Mode)', 'A'))">
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().ContainSingle();
    }

    [Fact]
    public void Analyze_ConditionalVersionUpdateWithInheritedVersionOverride_DoesNotReportSupersededFloat()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(2);
        declaredReferences.Should().OnlyContain(reference => reference.VersionOverride == "4.0.0");

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ConditionalVersionOnlyUpdateKeepsLatestSameScopeVersionOverride()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="2.*" />
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle(reference =>
            reference.VersionOverride == "2.*" && reference.IsConditional
        );

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        var issue = new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().ContainSingle().Subject;
        issue.Metadata!["versionSpecification"].Should().Be("2.*");
    }

    [Fact]
    public void Analyze_NarrowerConditionalVersionUpdateInheritsWiderScopeVersionOverride()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
                <PackageReference
                    Update="Serilog"
                    Condition="'$(Configuration)' == 'Debug'"
                    Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle(reference =>
            reference.Version == "4.*"
            && reference.VersionOverride == "4.0.0"
            && reference.IsConditional
        );

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_WiderConditionalVersionOverrideAmendsNarrowerProjection()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="4.*" />
                <PackageReference
                    Update="Serilog"
                    Condition="'$(Configuration)' == 'Debug'"
                    Version="4.*" />
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().OnlyContain(reference => reference.VersionOverride == "4.0.0");

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ExplicitEmptyVersionOverrideClearsPriorOverride()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="2.*" />
                <PackageReference Update="Serilog" Version="3.0.0" VersionOverride="" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle(reference =>
            reference.Version == "3.0.0" && reference.VersionOverride == null
        );

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ExplicitEmptyVersionOverrideOnlyClearsPriorOverride()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="2.*" />
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle(reference => reference.VersionOverride == null);

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ExplicitEmptyVersionClearsPriorVersion()
    {
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="2.*" />
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            </Project>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(projectPath);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle(reference => reference.Version == "");

        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declaredReferences
        );

        new FloatingVersionAnalyzer().Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not a version")]
    [InlineData("[1.0.0")]
    public void Analyze_AVersionNuGetCannotRead_IsNotThisRulesFinding(string version)
    {
        // Whatever is wrong with it, restore will say so loudly. Guessing here would report a
        // parse failure as a reproducibility problem.
        Analyze(DeclaredReference("Serilog", version)).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_AFloatingVersionOverride_IsReported()
    {
        // VersionOverride is NuGet's sanctioned way to step outside a central pin, so it is the
        // version actually in force — and it can float exactly as a Version can. Reading only
        // Version reported a project with an exact central pin and a floating override as clean.
        var reference = new PackageReference(
            "Serilog",
            string.Empty,
            Path.Combine(_testDirectory, "App.csproj"),
            "App.csproj",
            VersionOverride: "4.*"
        );

        var result = Analyze(reference);

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.PackageName.Should().Be("Serilog");
        issue.Metadata!["versionSpecification"].Should().Be("4.*");
    }

    [Fact]
    public void Analyze_AnExactVersionOverride_IsNotReported()
    {
        var reference = new PackageReference(
            "Serilog",
            string.Empty,
            Path.Combine(_testDirectory, "App.csproj"),
            "App.csproj",
            VersionOverride: "4.0.0"
        );

        Analyze(reference).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_AnExactOverrideOfAFloatingCentralPin_IsStillReportedForTheCentralPin()
    {
        // The override fixes it for one project; the pin every other project uses still floats.
        WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.*" />
              </ItemGroup>
            </Project>
            """
        );
        var reference = new PackageReference(
            "Serilog",
            string.Empty,
            Path.Combine(_testDirectory, "App.csproj"),
            "App.csproj",
            VersionOverride: "4.0.0"
        );

        Analyze(reference).Issues.Should().ContainSingle();
    }

    [Fact]
    public void Analyze_IsNotFixable()
    {
        // Choosing the concrete version a float should become needs the feed, which this pass does
        // not have. Claiming otherwise would have --fix print "no changes were needed" over it.
        var result = Analyze(DeclaredReference("Serilog", "4.*"));

        result.Issues.Should().ContainSingle().Which.Fixable.Should().BeFalse();
    }

    private AnalyzerResult Analyze(params PackageReference[] declared)
    {
        var packageInfo = new ProjectPackageInfo(
            References: Array.Empty<PackageReference>(),
            BasePath: _testDirectory,
            DeclaredReferences: declared
        );

        return new FloatingVersionAnalyzer().Analyze(packageInfo);
    }

    private PackageReference DeclaredReference(
        string package,
        string version,
        string projectFile = "App.csproj"
    ) => Reference(package, version, projectFile);

    private PackageReference Reference(
        string package,
        string version,
        string projectFile = "App.csproj"
    ) => new(package, version, Path.Combine(_testDirectory, projectFile), projectFile);

    private void WriteProps(string content) =>
        File.WriteAllText(Path.Combine(_testDirectory, CpmDriftAnalyzer.PropsFileName), content);
}
