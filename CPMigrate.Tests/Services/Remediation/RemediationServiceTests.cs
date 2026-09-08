using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Remediation;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services.Remediation;

/// <summary>
/// End-to-end guards for remediation, through the real backup manager and a real props file on disk
/// with the .NET CLI mocked.
///
/// The cases worth the setup cost are the ones where remediation must refuse to act: an incomplete
/// scan, and advisory data that could not be read. Both are situations where doing the obvious thing
/// — remediating what is known — produces a green run over a package that is still exposed.
/// </summary>
[Collection("Sequential")]
public sealed class RemediationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _propsPath;
    private readonly string _projectPath;

    public RemediationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateRemediate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        _propsPath = Path.Combine(_root, "Directory.Packages.props");
        File.WriteAllText(
            _propsPath,
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Vulnerable.Pkg" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        _projectPath = Path.Combine(_root, "src", "Api.csproj");
        File.WriteAllText(
            _projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Vulnerable.Pkg" /></ItemGroup>
            </Project>
            """
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private const string AdvisoryUrl = "https://github.com/advisories/GHSA-aaaa-bbbb-cccc";

    private VulnerabilityInfo Finding() =>
        new("Vulnerable.Pkg", "High", AdvisoryUrl, "1.0.0", string.Empty, "Api.csproj", _projectPath);

    /// <param name="record">The advisory to return, or null for no answer.</param>
    /// <param name="unreachable">
    /// Whether the absence is a transport failure. The oracle records those and leaves a definitive
    /// "not found" unrecorded, and that difference is what separates exit 8 from exit 10.
    /// </param>
    private sealed class StubOracle(AdvisoryRecord? record, bool unreachable = false) : IAdvisoryOracle
    {
        public IReadOnlyCollection<string> GetFailedLookups() =>
            unreachable ? ["GHSA-aaaa-bbbb-cccc"] : [];

        public Task<AdvisoryRecord?> LookupAsync(string advisoryId, string packageId) =>
            Task.FromResult(record);

        public void Dispose() { }
    }

    private sealed class StubVersionLookup(params string[] versions) : INuGetVersionLookupService
    {
        public IReadOnlyCollection<string> GetFailedLookups() => [];

        public Task<NuGetVersion?> GetLatestVersionAsync(string p, bool includePrerelease = false) =>
            Task.FromResult<NuGetVersion?>(null);

        public Task<NuGetVersion?> GetLatestVersionInMajorAsync(string p, int m, bool includePrerelease = false) =>
            Task.FromResult<NuGetVersion?>(null);

        public Task<IReadOnlyList<NuGetVersion>?> GetAllVersionsAsync(string packageId) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(versions.Select(NuGetVersion.Parse).ToList());

        public void Dispose() { }
    }

    private static AdvisoryRecord AdvisoryFixedIn(string fixedVersion) =>
        new(
            "GHSA-aaaa-bbbb-cccc",
            ["CVE-1111-2222"],
            "HIGH",
            [
                new AdvisoryVersionRange(
                    [new AdvisoryBoundary(NuGetVersion.Parse("0.0.0"), AdvisoryBoundaryKind.Introduced), new AdvisoryBoundary(NuGetVersion.Parse(fixedVersion), AdvisoryBoundaryKind.Fixed)]
                ),
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );

    private RemediationService BuildService(
        IAdvisoryOracle oracle,
        INuGetVersionLookupService versionLookup,
        Func<int, (List<VulnerabilityInfo>, bool)> scanByCall,
        bool testsPass = true
    )
    {
        var console = new FakeConsoleService { IsInteractive = false };

        var analyzer = new Mock<IProjectAnalyzer>();
        analyzer
            .Setup(a => a.DiscoverProjectsFromSolutionAsync(It.IsAny<string>()))
            .ReturnsAsync((_root, new List<string> { _projectPath }));

        var scanCall = 0;
        var query = new Mock<IDotNetPackageQueryService>();
        query
            .Setup(q => q.ScanVulnerabilitiesAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() => scanByCall(++scanCall));

        var cli = new Mock<IDotNetCliService>();
        cli.Setup(c => c.RunRestoreAsync(It.IsAny<string>())).ReturnsAsync((string.Empty, true));
        cli.Setup(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((testsPass ? string.Empty : "test failure", testsPass));

        return new RemediationService(
            console,
            analyzer.Object,
            query.Object,
            new PropsGenerator(new VersionResolver(console)),
            versionLookup,
            oracle,
            cli.Object,
            new BackupManager()
        );
    }

    private RemediateRequest Request(bool dryRun = false, bool allowMajor = false) =>
        new(
            SolutionPath: _root,
            AllowMajor: allowMajor,
            IncludePrerelease: false,
            DryRun: dryRun,
            Backup: new BackupSettings(true, Path.Combine(_root, ".backup"), false, _root),
            Output: new CommandOutput(OutputFormat.Json, Quiet: true, Force: true, OutputFile: null)
        );

    [Fact]
    public async Task AGreenRun_AppliesTheFixAndProvesItWithASecondScan()
    {
        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0", "1.9.0"),
            // First scan reports the advisory; the confirming scan reports none.
            call => call == 1 ? ([Finding()], true) : ([], true)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.AdvisoriesBefore.Should().Be(1);
        result.AdvisoriesAfter.Should().Be(0);
        result.ReVerified.Should().BeTrue();
        result.Remediated.Should().ContainSingle().Which.Should().Be("Vulnerable.Pkg");

        File.ReadAllText(_propsPath)
            .Should()
            .Contain("1.2.0", "the lowest clear version is what should land")
            .And.NotContain("1.9.0");
    }

    [Fact]
    public async Task AConfirmingScanThatStillReportsAdvisories_IsNotReportedAsSuccess()
    {
        // The whole point of re-scanning: the plan said this would clear, and it did not.
        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            _ => ([Finding()], true)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.RemediationIncomplete);
        result.AdvisoriesAfter.Should().Be(1);
    }

    [Fact]
    public async Task RedTests_RollBackTheFixAndLeaveThePropsFileExactlyAsItWas()
    {
        var before = File.ReadAllText(_propsPath);

        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            call => call == 1 ? ([Finding()], true) : ([], true),
            testsPass: false
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        result.WasRolledBack.Should().BeTrue();
        result.Remediated.Should().BeEmpty();
        File.ReadAllText(_propsPath).Should().Be(before);
    }

    [Fact]
    public async Task AnIncompleteVulnerabilityScan_ChangesNothingAndReportsExitEight()
    {
        var before = File.ReadAllText(_propsPath);

        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            _ => ([], false)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
        File.ReadAllText(_propsPath).Should().Be(before);
    }

    [Fact]
    public async Task AnUnreachableAdvisoryOracle_ChangesNothingRatherThanFallingBackToLatest()
    {
        // The behaviour that keeps an air-gapped CI honest: no advisory data means no proof, and an
        // unproven remediation must not be allowed to go green.
        var before = File.ReadAllText(_propsPath);

        using var service = BuildService(
            new StubOracle(null, unreachable: true),
            new StubVersionLookup("1.0.0", "1.2.0", "9.9.9"),
            call => call == 1 ? ([Finding()], true) : ([], true)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
        result.Actions.Should().ContainSingle();
        result.Actions[0].Outcome.Should().Be(RemediationOutcome.AdvisoryDataUnavailable);
        File.ReadAllText(_propsPath).Should().Be(before);
    }

    [Fact]
    public async Task AnAdvisoryTheDatabaseDoesNotCarry_ReportsIncompleteRatherThanTellingCiToReRun()
    {
        var before = File.ReadAllText(_propsPath);

        using var service = BuildService(
            new StubOracle(null),
            new StubVersionLookup("1.0.0", "1.2.0"),
            call => call == 1 ? ([Finding()], true) : ([], true)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.RemediationIncomplete, "a definitive 404 is a permanent answer");
        result.Actions[0].Outcome.Should().Be(RemediationOutcome.AdvisoryNotInDatabase);
        File.ReadAllText(_propsPath).Should().Be(before);
    }

    [Fact]
    public async Task ACleanSolution_PublishesAdvisoriesAfterZeroForTheGateToRead()
    {
        // README tells people to gate on advisoriesAfter == 0. Leaving it null on the cleanest
        // possible run drops the field from the payload and fails that gate.
        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            _ => ([], true)
        );

        var result = await service.RemediateAsync(Request());

        result.AdvisoriesAfter.Should().Be(0);
        result.ReVerified.Should().BeTrue();
    }

    [Fact]
    public async Task AScopedRun_JudgesItselfOnTheAdvisoriesItWasAskedAbout()
    {
        // --only narrows what is planned. Counting the whole workspace afterwards made a perfectly
        // successful scoped run see the untouched findings in the confirming scan and report itself
        // incomplete -- so it could never exit 0 unless the rest of the solution happened to be clean.
        var other = new VulnerabilityInfo(
            "Other.Pkg",
            "High",
            "https://github.com/advisories/GHSA-zzzz-yyyy-xxxx",
            "1.0.0",
            string.Empty,
            "Api.csproj",
            _projectPath
        );

        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            // The unselected package stays vulnerable in both scans; the selected one is cleared.
            call => call == 1 ? ([Finding(), other], true) : ([other], true)
        );

        var result = await service.RemediateAsync(Request() with { OnlyPackages = ["Vulnerable.Pkg"] });

        result.AdvisoriesBefore.Should().Be(1, "only the selected package is being answered for");
        result.AdvisoriesAfter.Should().Be(0);
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task ATransitiveOnlyFix_IsReportedRatherThanWrittenWhenPinningIsOff()
    {
        // A PackageVersion for a package nothing references directly does not move the resolved
        // graph unless the repo opts into transitive pinning. Writing one anyway leaves a dead entry
        // that looks like an applied fix, and it passes verification precisely because nothing changed.
        var before = File.ReadAllText(_propsPath);

        var transitiveFinding = new VulnerabilityInfo(
            "Vulnerable.Pkg",
            "High",
            AdvisoryUrl,
            "1.0.0",
            string.Empty,
            "Api.csproj",
            _projectPath,
            IsTransitive: true
        );

        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            call => call == 1 ? ([transitiveFinding], true) : ([], true)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.RemediationIncomplete);
        result.Actions[0].Outcome.Should().Be(RemediationOutcome.TransitivePinningDisabled);
        File.ReadAllText(_propsPath).Should().Be(before, "a pin that cannot govern the graph is not written");
    }

    [Fact]
    public async Task ATransitiveOnlyFix_IsWrittenWhenTheRepositoryEnablesPinning()
    {
        File.WriteAllText(
            _propsPath,
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Other.Pkg" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var transitiveFinding = new VulnerabilityInfo(
            "Vulnerable.Pkg",
            "High",
            AdvisoryUrl,
            "1.0.0",
            string.Empty,
            "Api.csproj",
            _projectPath,
            IsTransitive: true
        );

        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            call => call == 1 ? ([transitiveFinding], true) : ([], true)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.Success);
        File.ReadAllText(_propsPath).Should().Contain("Vulnerable.Pkg").And.Contain("1.2.0");
    }

    [Fact]
    public async Task ADryRun_ReportsThePlanWithoutTouchingTheFile()
    {
        var before = File.ReadAllText(_propsPath);

        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            _ => ([Finding()], true)
        );

        var result = await service.RemediateAsync(Request(dryRun: true));

        result.DryRun.Should().BeTrue();
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Actions[0].TargetVersion.Should().Be("1.2.0");
        File.ReadAllText(_propsPath).Should().Be(before);
    }

    [Fact]
    public async Task AMajorOnlyFix_IsWithheldWithoutAllowMajorAndAppliedWithIt()
    {
        var before = File.ReadAllText(_propsPath);

        using (
            var withheld = BuildService(
                new StubOracle(AdvisoryFixedIn("2.0.0")),
                new StubVersionLookup("1.0.0", "2.0.0"),
                call => call == 1 ? ([Finding()], true) : ([], true)
            )
        )
        {
            var result = await withheld.RemediateAsync(Request());

            result.ExitCode.Should().Be(ExitCodes.RemediationIncomplete);
            result.Actions[0].Outcome.Should().Be(RemediationOutcome.WithheldMajor);
            File.ReadAllText(_propsPath).Should().Be(before);
        }

        using var allowed = BuildService(
            new StubOracle(AdvisoryFixedIn("2.0.0")),
            new StubVersionLookup("1.0.0", "2.0.0"),
            call => call == 1 ? ([Finding()], true) : ([], true)
        );

        var applied = await allowed.RemediateAsync(Request(allowMajor: true));

        applied.ExitCode.Should().Be(ExitCodes.Success);
        File.ReadAllText(_propsPath).Should().Contain("2.0.0");
    }

    [Fact]
    public async Task ASolutionBelowTheGoverningPropsFile_IsRemediatedRatherThanCalledCpmDisabled()
    {
        // The monorepo layout: one Directory.Packages.props at the repository root, solutions in
        // subdirectories. MSBuild walks up to find it, so refusing here reported "CPM is not enabled"
        // about a repository where it plainly is.
        var nested = Path.Combine(_root, "services", "Billing");
        Directory.CreateDirectory(nested);

        var nestedProject = Path.Combine(nested, "Billing.csproj");
        File.WriteAllText(nestedProject, File.ReadAllText(_projectPath));

        var console = new FakeConsoleService { IsInteractive = false };
        var analyzer = new Mock<IProjectAnalyzer>();
        analyzer
            .Setup(a => a.DiscoverProjectsFromSolutionAsync(It.IsAny<string>()))
            .ReturnsAsync((nested, new List<string> { nestedProject }));

        var query = new Mock<IDotNetPackageQueryService>();
        var scanCall = 0;
        query
            .Setup(q => q.ScanVulnerabilitiesAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() =>
                ++scanCall == 1
                    ? (new List<VulnerabilityInfo> { Finding() }, true)
                    : (new List<VulnerabilityInfo>(), true)
            );

        var cli = new Mock<IDotNetCliService>();
        cli.Setup(c => c.RunRestoreAsync(It.IsAny<string>())).ReturnsAsync((string.Empty, true));
        cli.Setup(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string.Empty, true));

        using var service = new RemediationService(
            console,
            analyzer.Object,
            query.Object,
            new PropsGenerator(new VersionResolver(console)),
            new StubVersionLookup("1.0.0", "1.2.0"),
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            cli.Object,
            new BackupManager()
        );

        var result = await service.RemediateAsync(Request() with { SolutionPath = nested });

        result.ExitCode.Should().Be(ExitCodes.Success);
        File.ReadAllText(_propsPath).Should().Contain("1.2.0", "the ancestor props file is the one that governs");
    }

    [Fact]
    public async Task AMisspelledOnlyName_IsRejectedRatherThanReportedClean()
    {
        // The dangerous case: --only Newtonsof.Json over a workspace whose Newtonsoft.Json has a live
        // CVE produces no findings for that name and would exit 0, taking a CI gate green over an
        // unremediated advisory. A clean package and a typo are indistinguishable by findings alone,
        // so the name is checked against the workspace instead.
        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            _ => ([Finding()], true)
        );

        var result = await service.RemediateAsync(
            Request() with
            {
                OnlyPackages = ["Vulnerabl.Pkg"],
            }
        );

        result.ExitCode.Should().Be(ExitCodes.ValidationError);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Vulnerabl.Pkg");
    }

    [Fact]
    public async Task AnOnlyNameThatIsPinnedButHasNoAdvisory_IsAcceptedAsAlreadyClean()
    {
        // The legitimate twin of the case above: the package exists, it just has nothing wrong with
        // it. That has to stay a clean exit 0, or narrowing a run to a healthy package would fail.
        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            _ => ([Finding()], true)
        );

        var result = await service.RemediateAsync(
            Request() with
            {
                OnlyPackages = ["Vulnerable.Pkg"],
            }
        );

        result.ExitCode.Should().NotBe(ExitCodes.ValidationError);
    }

    [Fact]
    public async Task NoAdvisories_ReportsSuccessWithoutRunningTests()
    {
        using var service = BuildService(
            new StubOracle(AdvisoryFixedIn("1.2.0")),
            new StubVersionLookup("1.0.0", "1.2.0"),
            _ => ([], true)
        );

        var result = await service.RemediateAsync(Request());

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.VerificationRuns.Should().Be(0);
    }
}
