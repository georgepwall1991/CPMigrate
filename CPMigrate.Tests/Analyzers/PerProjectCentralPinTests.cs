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
    public void PathComparerFor_ReflectsFilesystemCaseSensitivity_AndCleansItsProbe()
    {
        var comparer = CpmDriftAnalyzer.PathComparerFor(_root);
        var markerDirectory = Path.Combine(_root, $"case-marker-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(markerDirectory);
            var lower = Path.Combine(markerDirectory, "marker");
            File.WriteAllText(lower, string.Empty);
            var upperExists = File.Exists(Path.Combine(markerDirectory, "MARKER"));

            comparer
                .Should()
                .BeSameAs(
                    upperExists ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal,
                    "path identity must follow the filesystem, not the host operating system"
                );
        }
        finally
        {
            if (Directory.Exists(markerDirectory))
            {
                Directory.Delete(markerDirectory, recursive: true);
            }
        }

        Directory
            .GetDirectories(_root, ".cpmigrate-case-probe-*")
            .Should()
            .BeEmpty("case detection must not leave files in the scanned tree");
    }

    [Fact]
    public void PathComparerFor_UsesOrdinalWhenTheScanRootCannotBeProbed()
    {
        CpmDriftAnalyzer
            .PathComparerFor(Path.Combine(_root, "does-not-exist"))
            .Should()
            .BeSameAs(StringComparer.Ordinal);
    }

    [Fact]
    public void Analyze_CaseDistinctPropsDirectories_FollowsFilesystemIdentity()
    {
        // On a case-sensitive volume these are two independent MSBuild contexts. On a
        // case-insensitive volume they intentionally collapse to one physical props file; the test
        // asserts that observed result too, rather than silently returning with no assertion.
        WriteProps("tools", ("Lower.Only", "1.0.0"));
        WriteProps("Tools", ("Upper.Only", "2.0.0"));
        var lowerProject = WriteProject("tools/Lower/Lower.csproj", "Lower.Only");
        var upperProject = WriteProject("Tools/Upper/Upper.csproj", "Upper.Only");

        var comparer = CpmDriftAnalyzer.PathComparerFor(_root);
        var lowerDirectory = Path.GetFullPath(Path.Combine(_root, "tools"));
        var upperDirectory = Path.GetFullPath(Path.Combine(_root, "Tools"));
        var caseDistinct = !comparer.Equals(lowerDirectory, upperDirectory);
        var issues = Analyze(lowerProject, upperProject);

        if (caseDistinct)
        {
            issues
                .Should()
                .NotContain(issue =>
                    issue.IssueCode == AnalysisIssueCode.MissingPackageVersion
                    && (issue.PackageName == "Lower.Only" || issue.PackageName == "Upper.Only")
                );
        }
        else
        {
            issues
                .Should()
                .Contain(issue =>
                    issue.IssueCode == AnalysisIssueCode.MissingPackageVersion
                    && issue.PackageName == "Lower.Only"
                );
            issues
                .Should()
                .NotContain(issue =>
                    issue.IssueCode == AnalysisIssueCode.MissingPackageVersion
                    && issue.PackageName == "Upper.Only"
                );
        }

        var pins = CpmDriftAnalyzer.ReadEffectiveCentralVersions(
            _root,
            new[] { lowerProject, upperProject }
        );

        pins.Should().Contain(pin => pin.Package == "Upper.Only");
        if (caseDistinct)
        {
            pins.Should().Contain(pin => pin.Package == "Lower.Only");
        }
        else
        {
            pins.Should().NotContain(pin => pin.Package == "Lower.Only");
        }
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
    public void ReadEffectiveCentralVersions_ARootExactPinDoesNotHideANestedFloatingOne()
    {
        // Collapsing to one entry per package let the root's exact Serilog pin mask the nested
        // file's 4.*, so the nested project's dependency passed as reproducible when it is not —
        // exactly the finding FloatingVersion exists to make.
        WriteProps(".", ("Serilog", "4.0.0"));
        WriteProps("tools", ("Serilog", "4.*"));
        var rootProject = WriteProject("src/Api/Api.csproj", "Serilog");
        var toolProject = WriteProject("tools/Build/Build.csproj", "Serilog");

        var pins = CpmDriftAnalyzer.ReadEffectiveCentralVersions(
            _root,
            new[] { rootProject, toolProject }
        );

        pins.Should().Contain(pin => pin.Package == "Serilog" && pin.Version == "4.*");
        pins.Should().Contain(pin => pin.Package == "Serilog" && pin.Version == "4.0.0");
    }

    [Fact]
    public void Analyze_EnablementFromANearerDirectoryBuildProps_IsHonoured()
    {
        // The property is resolved from the governed project's own directory. Reading it from the
        // scan root reported a valid project as CpmNotEnabled — a High finding that fails CI.
        File.WriteAllText(
            Path.Combine(_root, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        Directory.CreateDirectory(Path.Combine(_root, "tools"));
        File.WriteAllText(
            Path.Combine(_root, "tools", "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        File.WriteAllText(
            Path.Combine(_root, "tools", CpmDriftAnalyzer.PropsFileName),
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var project = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(project);

        issues.Should().NotContain(issue => issue.IssueCode == AnalysisIssueCode.CpmNotEnabled);
    }

    [Fact]
    public void Analyze_TwoProjectsSharingAPropsFileWithDifferentEnablement_AreJudgedSeparately()
    {
        // Caching enablement by props file alone let whichever project came first decide for both:
        // if the disabled one was first, a valid project was skipped entirely.
        WriteBuildProps("src/Exempt", enabled: false);
        WriteProps(".", ("Serilog", "4.0.0"));
        var governed = WriteProject("src/Api/Api.csproj", "Serilog");
        var exempt = WriteProject("src/Exempt/Exempt.csproj", "Serilog");

        // The exempt project comes first, which is what used to poison the shared cache entry.
        var issues = Analyze(exempt, governed);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion);
    }

    [Fact]
    public void Analyze_TwoUnusablePropsFiles_AreDistinctFindings()
    {
        // Each names the props file as its package and no project at all, so without the file in
        // the identity a baseline accepting one would silently suppress the other.
        WriteDisabledProps(".");
        WriteDisabledProps("tools");
        var rootProject = WriteProject("src/Api/Api.csproj", "Serilog");
        var toolProject = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(rootProject, toolProject);

        var fingerprints = issues
            .Where(issue => issue.IssueCode == AnalysisIssueCode.CpmNotEnabled)
            .Select(AnalysisIssueIdentity.Compute)
            .ToList();

        fingerprints.Should().HaveCount(2);
        fingerprints.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_APinInheritedByANestedFile_IsNotOrphanedByTheFileThatDoesNotUseIt()
    {
        // The nested file imports the root, so the root's pin appears in both central sets. Judging
        // each set alone had the root context call it orphaned — the only project using it is
        // governed by the nested file — and the nested context call it orphaned in reverse.
        WriteProps(".", ("Serilog", "4.0.0"));
        WriteImportingProps("tools", "../Directory.Packages.props");
        var rootProject = WriteProject("src/Api/Api.csproj", "Newtonsoft.Json");
        var toolProject = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(rootProject, toolProject);

        issues
            .Should()
            .NotContain(issue =>
                issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion
                && issue.PackageName == "Serilog"
            );
    }

    [Fact]
    public void Analyze_APinDeclaredInAnImportedFile_NamesThatFile()
    {
        // Reporting it against the importing file points at the wrong place to edit — the pin is
        // not in Directory.Packages.props at all.
        WriteImportingProps(".", "Versions.props");
        File.WriteAllText(
            Path.Combine(_root, "Versions.props"),
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Unused.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var project = WriteProject("src/Api/Api.csproj", "Newtonsoft.Json");

        var issues = Analyze(project);

        issues
            .Should()
            .Contain(issue =>
                issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion
                && issue.PackageName == "Unused.Package"
                && issue.Description.Contains("Versions.props")
            );
    }

    [Fact]
    public void Analyze_AProjectMissingAPin_IsToldToEditTheFileThatGovernsIt()
    {
        // Naming the root file told a project governed by a nested one to edit a file MSBuild never
        // reads for it.
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"));
        WriteProps("tools", ("Serilog", "4.0.0"));
        var project = WriteProject("tools/Build/Build.csproj", "NotPinned.Anywhere");

        var issues = Analyze(project);

        issues
            .Should()
            .Contain(issue =>
                issue.IssueCode == AnalysisIssueCode.MissingPackageVersion
                && issue.Description.Contains("tools/Directory.Packages.props")
            );
    }

    [Fact]
    public void Analyze_AReferenceUnderANonImportingFile_IsNotEvidenceForAnotherFilesPin()
    {
        // Pooling references across every context let a Serilog reference in the nested file's
        // scope vouch for the root's independent Serilog pin, suppressing a real orphan.
        WriteProps(".", ("Serilog", "4.0.0"), ("Newtonsoft.Json", "13.0.1"));
        WriteProps("tools", ("Serilog", "4.0.0"));
        var rootProject = WriteProject("src/Api/Api.csproj", "Newtonsoft.Json");
        var toolProject = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(rootProject, toolProject);

        issues
            .Should()
            .Contain(issue =>
                issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion
                && issue.PackageName == "Serilog"
                && issue.Description.Contains("Directory.Packages.props")
                && !issue.Description.Contains("tools/")
            );
    }

    [Fact]
    public void Analyze_APinUsedOnlyUnderTransitivePinning_IsNotOrphaned()
    {
        // A context with transitive pinning on originates no orphan findings, but its references are
        // still proof the pin is used. Dropping them alongside its pins let a package referenced only
        // there be reported orphaned against the root file that declares it.
        WriteProps(".", ("Serilog", "4.0.0"), ("Newtonsoft.Json", "13.0.1"));
        WriteTransitivePinningProps("tools", "../Directory.Packages.props");
        var rootProject = WriteProject("src/Api/Api.csproj", "Newtonsoft.Json");
        var toolProject = WriteProject("tools/Build/Build.csproj", "Serilog");

        var issues = Analyze(rootProject, toolProject);

        issues
            .Should()
            .NotContain(issue =>
                issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion
                && issue.PackageName == "Serilog"
            );
    }

    [Fact]
    public void Analyze_AProjectOptingOutInItsOwnFile_IsNotJudgedAgainstCentralPins()
    {
        // MSBuild imports Directory.Build.props at the top of the project, so the project's own body
        // has the last word on ManagePackageVersionsCentrally. Reading enablement only from the props
        // files reported every ordinary inline version in an opted-out project as drift.
        WriteProps(".", ("Serilog", "4.0.0"));
        var project = WriteProject(
            "src/Legacy/Legacy.csproj",
            "Serilog",
            inlineVersion: "3.1.1",
            centrallyManaged: false
        );

        var issues = Analyze(project);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.InlineVersionUnderCpm);
        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_AProjectOptingInInItsOwnFile_IsStillGoverned()
    {
        // The same override the other way: a project turning central management on for itself must
        // not be dismissed as CpmNotEnabled and skipped. The props file stays silent on the property
        // so enablement really is decided by the build props and the project, not short-circuited.
        WriteSilentProps(".", ("Serilog", "4.0.0"));
        WriteBuildProps("src", enabled: false);
        var project = WriteProject(
            "src/Api/Api.csproj",
            "Serilog",
            inlineVersion: "3.1.1",
            centrallyManaged: true
        );

        var issues = Analyze(project);

        issues
            .Should()
            .Contain(issue => issue.IssueCode == AnalysisIssueCode.InlineVersionUnderCpm);
    }

    [Fact]
    public void Analyze_AProjectRedirectingItsPropsFile_IsJudgedAgainstThatFile()
    {
        // DirectoryPackagesPropsPath makes MSBuild import the named file instead of the nearest
        // conventional one. Judging the project against Directory.Packages.props measured it against
        // pins it never receives, and named a file MSBuild does not read for it.
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"));
        WriteRedirect("src", "$(MSBuildThisFileDirectory)Packages.props");
        WriteNamedProps("src/Packages.props", ("Serilog", "4.0.0"));
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        var issues = Analyze(project);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_AProjectRedirectingToAPathItCannotResolve_IsLeftAlone()
    {
        // The redirect names a property this cannot evaluate, so which file governs the project is
        // genuinely unknown. Falling back to the conventional file would judge it against pins it may
        // never receive — saying nothing is the only honest answer.
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"));
        WriteRedirect("src", "$(SomethingOnlyMSBuildKnows)/Packages.props");
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        var issues = Analyze(project);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ARedirectDeclaredInAnImportedFile_IsStillFollowed()
    {
        // MSBuild observes a redirect wherever an unconditional import puts it. Reading only the
        // outer document's descendants fell back to the conventional file and judged the project
        // against pins it never receives — the same defect the redirect support exists to remove.
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"));
        WriteImportingBuildProps("src", "Shared.props");
        WriteRedirectFragment("src/Shared.props", "$(MSBuildThisFileDirectory)Packages.props");
        WriteNamedProps("src/Packages.props", ("Serilog", "4.0.0"));
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        var issues = Analyze(project);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void ReadEffectiveCentralVersions_AProjectOptingOut_ContributesNoPins()
    {
        // Floating-pin discovery resolved enablement from the project's directory alone, so the
        // project's own opt-out never reached it and inert central pins were still reported.
        WriteProps(".", ("Serilog", "4.*"));
        var project = WriteProject(
            "src/Legacy/Legacy.csproj",
            "Serilog",
            inlineVersion: "3.1.1",
            centrallyManaged: false
        );

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(_root, [project]).Should().BeEmpty();
    }

    [Fact]
    public void ReadEffectiveCentralVersions_AProjectOptingIn_ContributesItsPins()
    {
        // And the other way: a project turning central management on for itself really is governed,
        // so a floating pin in the file governing it is a finding FloatingVersion must still make.
        WriteSilentProps(".", ("Serilog", "4.*"));
        WriteBuildProps("src", enabled: false);
        var project = WriteProject("src/Api/Api.csproj", "Serilog", centrallyManaged: true);

        CpmDriftAnalyzer
            .ReadEffectiveCentralVersions(_root, [project])
            .Should()
            .Contain(pin => pin.Package == "Serilog");
    }

    [Fact]
    public void Analyze_EnablementSuppliedOnlyByAnImportedFragment_CountsAsEnabled()
    {
        // The fragment supplies both the redirect and the enablement, and the redirected file does
        // not repeat the property. Reading the build props without following its imports reported
        // CpmNotEnabled — a High finding — and dropped its pins, on a repository MSBuild manages
        // perfectly well.
        WriteImportingBuildProps("src", "Shared.props");
        WriteRedirectFragment("src/Shared.props", "$(MSBuildThisFileDirectory)Packages.props");
        WriteSilentNamedProps("src/Packages.props", ("Serilog", "4.0.0"));
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        var issues = Analyze(project);

        issues.Should().NotContain(issue => issue.IssueCode == AnalysisIssueCode.CpmNotEnabled);
        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_ARedirectBehindAPropertyAnchoredImport_IsStillFollowed()
    {
        // $(MSBuildThisFileDirectory) is statically known, and anchoring an import with it is the
        // ordinary way to write a portable one. Rejecting the path for containing '$(' left the
        // redirect unread and fell back to the conventional file.
        WriteProps(".", ("Newtonsoft.Json", "13.0.1"));
        WriteImportingBuildProps("src", "$(MSBuildThisFileDirectory)Shared.props");
        WriteRedirectFragment("src/Shared.props", "$(MSBuildThisFileDirectory)Packages.props");
        WriteNamedProps("src/Packages.props", ("Serilog", "4.0.0"));
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        var issues = Analyze(project);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_ARedirectInsideAConditionedImportGroup_IsNotFollowed()
    {
        // Whether the group applies depends on properties this cannot evaluate, so following it
        // could swap in an unrelated pin set — the same reason a conditioned Import is skipped. The
        // conventional file governs instead, and it pins what the project references.
        WriteProps(".", ("Serilog", "4.0.0"));
        WriteConditionedGroupBuildProps("src", "Shared.props");
        WriteRedirectFragment("src/Shared.props", "$(MSBuildThisFileDirectory)Packages.props");
        WriteNamedProps("src/Packages.props", ("Newtonsoft.Json", "13.0.1"));
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        var issues = Analyze(project);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion);
    }

    [Fact]
    public void Analyze_AProjectDelegatingItsOptOutToAnImport_IsNotJudgedAgainstCentralPins()
    {
        // A project can put its opt-out in an imported fragment as readily as in its own body, and
        // MSBuild sees no difference. Reading only the project's own descendants judged it centrally
        // managed and reported its ordinary inline versions as drift.
        WriteProps(".", ("Serilog", "4.0.0"));
        WriteEnablementFragment("src/Legacy/Exempt.props", enabled: false);
        var project = WriteImportingProject(
            "src/Legacy/Legacy.csproj",
            "Serilog",
            "3.1.1",
            "Exempt.props"
        );

        var issues = Analyze(project);

        issues
            .Should()
            .NotContain(issue => issue.IssueCode == AnalysisIssueCode.InlineVersionUnderCpm);
    }

    [Fact]
    public void ReadEffectiveCentralVersions_AProjectDelegatingItsOptInToAnImport_ContributesItsPins()
    {
        // And the inverse, through the reader floating-pin discovery uses.
        WriteSilentProps(".", ("Serilog", "4.*"));
        WriteBuildProps("src", enabled: false);
        WriteEnablementFragment("src/Api/Governed.props", enabled: true);
        var project = WriteImportingProject(
            "src/Api/Api.csproj",
            "Serilog",
            "3.1.1",
            "Governed.props"
        );

        CpmDriftAnalyzer
            .ReadEffectiveCentralVersions(_root, [project])
            .Should()
            .Contain(pin => pin.Package == "Serilog");
    }

    [Fact]
    public void Analyze_ARedirectedFileOutsideTheScanRoot_IsNamedByItsPath()
    {
        // Escaping the scan root, the conventional name is a fair label for the conventional file —
        // there is only one meaning for it. For a redirected custom file it names a file that does
        // not exist, and two different custom files collapse onto one identity.
        WriteNamedProps("shared/Packages.props", ("Newtonsoft.Json", "13.0.1"));
        WriteRedirect("scan", "../shared/Packages.props");
        var project = WriteProject("scan/src/Api/Api.csproj", "Serilog");

        var issues = AnalyzeFrom(Path.Combine(_root, "scan"), project);

        issues
            .Should()
            .Contain(issue =>
                issue.IssueCode == AnalysisIssueCode.MissingPackageVersion
                && issue.Description.Contains("shared/Packages.props")
            );
    }

    [Fact]
    public void Analyze_TwoRedirectsToLikeNamedFilesOutsideTheScanRoot_AreDistinctFindings()
    {
        // Both redirected files carry the conventional name but are different files. Labelling each
        // by the convention gave them one identity, so a baseline accepting one would suppress the
        // other — and neither finding named the file it meant.
        WriteNamedProps("left/Directory.Packages.props", ("Newtonsoft.Json", "13.0.1"));
        WriteNamedProps("right/Directory.Packages.props", ("Newtonsoft.Json", "13.0.1"));
        WriteRedirect("scan/a", "../../left/Directory.Packages.props");
        WriteRedirect("scan/b", "../../right/Directory.Packages.props");
        var left = WriteProject("scan/a/Api/Api.csproj", "Serilog");
        var right = WriteProject("scan/b/Api/Api.csproj", "Serilog");

        var issues = AnalyzeFrom(Path.Combine(_root, "scan"), left, right);

        var described = issues
            .Where(issue => issue.IssueCode == AnalysisIssueCode.MissingPackageVersion)
            .Select(issue => issue.Description)
            .ToList();

        described.Should().HaveCount(2);
        described.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_NoPropsFileAnywhere_ReportsNothing()
    {
        var project = WriteProject("src/Api/Api.csproj", "Serilog");

        Analyze(project).Should().BeEmpty();
    }

    private IReadOnlyList<AnalysisIssue> Analyze(params string[] projectPaths)
    {
        return AnalyzeFrom(_root, projectPaths);
    }

    /// <summary>Analyses with the scan rooted somewhere other than the fixture root.</summary>
    private static IReadOnlyList<AnalysisIssue> AnalyzeFrom(
        string basePath,
        params string[] projectPaths
    )
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
            BasePath: basePath,
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

    private void WriteImportingProps(string relativeDirectory, string import)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, CpmDriftAnalyzer.PropsFileName),
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <Import Project="{import}" />
            </Project>
            """
        );
    }

    /// <summary>
    /// A <c>Directory.Build.props</c> redirecting central management to a file of its own choosing.
    /// </summary>
    private void WriteRedirect(string relativeDirectory, string propsPath)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Directory.Build.props"),
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <DirectoryPackagesPropsPath>{propsPath}</DirectoryPackagesPropsPath>
              </PropertyGroup>
            </Project>
            """
        );
    }

    /// <summary>
    /// A <c>Directory.Build.props</c> that delegates its settings to an unconditional import.
    /// </summary>
    private void WriteImportingBuildProps(string relativeDirectory, string import)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Directory.Build.props"),
            $"""
            <Project>
              <Import Project="{import}" />
            </Project>
            """
        );
    }

    /// <summary>An imported fragment carrying the redirect and the enablement property.</summary>
    private void WriteRedirectFragment(string relativePath, string propsPath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <DirectoryPackagesPropsPath>{propsPath}</DirectoryPackagesPropsPath>
              </PropertyGroup>
            </Project>
            """
        );
    }

    /// <summary>
    /// A <c>Directory.Build.props</c> whose import sits inside a conditioned <c>ImportGroup</c>.
    /// </summary>
    private void WriteConditionedGroupBuildProps(string relativeDirectory, string import)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Directory.Build.props"),
            $"""
            <Project>
              <ImportGroup Condition="'$(Flavour)' == 'Internal'">
                <Import Project="{import}" />
              </ImportGroup>
            </Project>
            """
        );
    }

    /// <summary>A fragment carrying nothing but the enablement property, for a project to import.</summary>
    private void WriteEnablementFragment(string relativePath, bool enabled)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>{(
                enabled ? "true" : "false"
            )}</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
    }

    /// <summary>A project that delegates its properties to an unconditional import.</summary>
    private string WriteImportingProject(
        string relativePath,
        string package,
        string inlineVersion,
        string import
    )
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{import}" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{package}" Version="{inlineVersion}" />
              </ItemGroup>
            </Project>
            """
        );

        return path;
    }

    /// <summary>Pins in a file that is not named <c>Directory.Packages.props</c>.</summary>
    private void WriteNamedProps(
        string relativePath,
        params (string Package, string Version)[] pins
    )
    {
        WriteNamedPropsFile(relativePath, declaresEnablement: true, pins);
    }

    /// <summary>
    /// The same, without repeating the enablement property — so a test that needs enablement to
    /// come from elsewhere cannot pass on the redirected file quietly supplying it.
    /// </summary>
    private void WriteSilentNamedProps(
        string relativePath,
        params (string Package, string Version)[] pins
    )
    {
        WriteNamedPropsFile(relativePath, declaresEnablement: false, pins);
    }

    private void WriteNamedPropsFile(
        string relativePath,
        bool declaresEnablement,
        (string Package, string Version)[] pins
    )
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var items = string.Join(
            "\n    ",
            pins.Select(pin =>
                $"""<PackageVersion Include="{pin.Package}" Version="{pin.Version}" />"""
            )
        );

        var properties = declaresEnablement
            ? "\n  <PropertyGroup>\n    "
                + "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
                + "\n  </PropertyGroup>"
            : string.Empty;

        File.WriteAllText(
            path,
            $"""
            <Project>{properties}
              <ItemGroup>
                {items}
              </ItemGroup>
            </Project>
            """
        );
    }

    private void WriteTransitivePinningProps(string relativeDirectory, string import)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, CpmDriftAnalyzer.PropsFileName),
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
              <Import Project="{import}" />
            </Project>
            """
        );
    }

    private void WriteBuildProps(string relativeDirectory, bool enabled)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Directory.Build.props"),
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>{(
                enabled ? "true" : "false"
            )}</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
    }

    private void WriteDisabledProps(string relativeDirectory)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, CpmDriftAnalyzer.PropsFileName),
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
    }

    private void WriteProps(
        string relativeDirectory,
        params (string Package, string Version)[] pins
    )
    {
        WritePropsFile(relativeDirectory, declaresEnablement: true, pins);
    }

    /// <summary>
    /// Pins with no <c>ManagePackageVersionsCentrally</c> of its own, so enablement is decided by
    /// the build props and the project rather than short-circuited by the props file.
    /// </summary>
    private void WriteSilentProps(
        string relativeDirectory,
        params (string Package, string Version)[] pins
    )
    {
        WritePropsFile(relativeDirectory, declaresEnablement: false, pins);
    }

    private void WritePropsFile(
        string relativeDirectory,
        bool declaresEnablement,
        (string Package, string Version)[] pins
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

        var properties = declaresEnablement
            ? "\n  <PropertyGroup>\n    "
                + "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
                + "\n  </PropertyGroup>"
            : string.Empty;

        File.WriteAllText(
            Path.Combine(directory, CpmDriftAnalyzer.PropsFileName),
            $"""
            <Project>{properties}
              <ItemGroup>
                {items}
              </ItemGroup>
            </Project>
            """
        );
    }

    private string WriteProject(
        string relativePath,
        string package,
        string? inlineVersion = null,
        bool? centrallyManaged = null
    )
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var enablement = centrallyManaged is null
            ? string.Empty
            : $"\n    <ManagePackageVersionsCentrally>{(
                centrallyManaged.Value ? "true" : "false"
            )}</ManagePackageVersionsCentrally>";

        var version = inlineVersion is null ? string.Empty : $" Version=\"{inlineVersion}\"";

        File.WriteAllText(
            path,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>{enablement}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{package}"{version} />
              </ItemGroup>
            </Project>
            """
        );

        return path;
    }
}
