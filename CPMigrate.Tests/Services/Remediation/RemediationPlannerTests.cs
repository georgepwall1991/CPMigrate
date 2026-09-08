using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Remediation;
using FluentAssertions;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services.Remediation;

/// <summary>
/// Guards the plan: which packages get moved, which get reported instead, and — the case with a
/// security consequence — when the planner refuses to answer at all.
/// </summary>
public class RemediationPlannerTests
{
    private const string AdvisoryUrl = "https://github.com/advisories/GHSA-aaaa-bbbb-cccc";

    private sealed class FakeOracle : IAdvisoryOracle
    {
        private readonly Dictionary<string, AdvisoryRecord?> _records = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _failed = [];

        public FakeOracle Knows(string advisoryId, string fixedVersion, string? cve = "CVE-1111-2222")
        {
            _records[advisoryId] = new AdvisoryRecord(
                advisoryId,
                cve is null ? [] : [cve],
                "HIGH",
                [
                    new AdvisoryVersionRange(
                        [(NuGetVersion.Parse("0.0.0"), true), (NuGetVersion.Parse(fixedVersion), false)]
                    ),
                ],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            );
            return this;
        }

        public FakeOracle CannotAnswer(string advisoryId)
        {
            _records[advisoryId] = null;
            _failed.Add(advisoryId);
            return this;
        }

        public IReadOnlyCollection<string> GetFailedLookups() => _failed;

        public Task<AdvisoryRecord?> LookupAsync(string advisoryId, string packageId)
        {
            var id = OsvAdvisoryOracle.ExtractAdvisoryId(advisoryId);
            return Task.FromResult(_records.TryGetValue(id, out var record) ? record : null);
        }

        public void Dispose() { }
    }

    private sealed class FakeVersionLookup : INuGetVersionLookupService
    {
        private readonly Dictionary<string, IReadOnlyList<NuGetVersion>?> _versions = new(
            StringComparer.OrdinalIgnoreCase
        );

        public FakeVersionLookup Publishes(string packageId, params string[] versions)
        {
            _versions[packageId] = versions.Select(NuGetVersion.Parse).ToList();
            return this;
        }

        public IReadOnlyCollection<string> GetFailedLookups() => [];

        public Task<NuGetVersion?> GetLatestVersionAsync(string packageId, bool includePrerelease = false) =>
            Task.FromResult<NuGetVersion?>(null);

        public Task<NuGetVersion?> GetLatestVersionInMajorAsync(
            string packageId,
            int majorVersion,
            bool includePrerelease = false
        ) => Task.FromResult<NuGetVersion?>(null);

        public Task<IReadOnlyList<NuGetVersion>?> GetAllVersionsAsync(string packageId) =>
            Task.FromResult(_versions.TryGetValue(packageId, out var v) ? v : null);

        public void Dispose() { }
    }

    private static VulnerabilityInfo Finding(
        string package,
        string resolvedVersion,
        string projectName = "Api.csproj",
        bool isTransitive = false,
        string advisoryUrl = AdvisoryUrl,
        string severity = "High"
    ) => new(package, severity, advisoryUrl, resolvedVersion, string.Empty, projectName, $"/repo/{projectName}", isTransitive);

    [Fact]
    public async Task AnInMajorFix_IsPlannedWithTheLowestClearVersion()
    {
        var oracle = new FakeOracle().Knows("GHSA-aaaa-bbbb-cccc", "1.2.0");
        var lookup = new FakeVersionLookup().Publishes("Pkg", "1.0.0", "1.2.0", "1.9.0");
        var planner = new RemediationPlanner(oracle, lookup);

        var plan = await planner.PlanAsync(
            [Finding("Pkg", "1.0.0")],
            allowMajor: false,
            includePrerelease: false
        );

        var action = plan.Actions.Should().ContainSingle().Subject;
        action.Outcome.Should().Be(RemediationOutcome.Planned);
        action.TargetVersion.Should().Be("1.2.0");
        action.IsMajorBump.Should().BeFalse();
        action.Cves.Should().Contain("CVE-1111-2222");
        plan.GetApplicable().Should().ContainSingle();
    }

    [Fact]
    public async Task AMajorOnlyFix_IsWithheldByDefaultAndAppliedUnderAllowMajor()
    {
        var oracle = new FakeOracle().Knows("GHSA-aaaa-bbbb-cccc", "13.0.1");
        var lookup = new FakeVersionLookup().Publishes("Pkg", "9.0.1", "13.0.1", "13.0.4");
        var planner = new RemediationPlanner(oracle, lookup);
        var findings = new[] { Finding("Pkg", "9.0.1") };

        var withheld = await planner.PlanAsync(findings, allowMajor: false, includePrerelease: false);
        var applied = await planner.PlanAsync(findings, allowMajor: true, includePrerelease: false);

        withheld.Actions[0].Outcome.Should().Be(RemediationOutcome.WithheldMajor);
        withheld.Actions[0].TargetVersion.Should().Be("13.0.1", "the target is still reported so the decision can be made");
        withheld.Actions[0].Reason.Should().Contain("--allow-major");
        withheld.GetApplicable().Should().BeEmpty();

        applied.Actions[0].Outcome.Should().Be(RemediationOutcome.Planned);
        applied.Actions[0].IsMajorBump.Should().BeTrue();
    }

    [Fact]
    public async Task AnAdvisoryThatCannotBeLookedUp_StopsThePackageBeingPlannedAtAll()
    {
        // The dangerous shape: a version clearing the advisories that *could* be read may sit squarely
        // inside the range of the one that could not. A target computed here would carry the
        // authority of a proof it does not have.
        var oracle = new FakeOracle()
            .Knows("GHSA-aaaa-bbbb-cccc", "1.2.0")
            .CannotAnswer("GHSA-dddd-eeee-ffff");
        var lookup = new FakeVersionLookup().Publishes("Pkg", "1.0.0", "1.2.0", "2.0.0");
        var planner = new RemediationPlanner(oracle, lookup);

        var plan = await planner.PlanAsync(
            [
                Finding("Pkg", "1.0.0"),
                Finding("Pkg", "1.0.0", advisoryUrl: "https://github.com/advisories/GHSA-dddd-eeee-ffff"),
            ],
            allowMajor: false,
            includePrerelease: false
        );

        plan.Actions.Should().ContainSingle();
        plan.Actions[0].Outcome.Should().Be(RemediationOutcome.AdvisoryDataUnavailable);
        plan.Actions[0].TargetVersion.Should().BeNull();
        plan.HasUnavailableAdvisoryData.Should().BeTrue();
        plan.GetApplicable().Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameAdvisoryReportedPerFramework_IsPlannedOnce()
    {
        // A multi-target project yields one finding per framework. A version is pinned once,
        // centrally, so planning them separately would queue the same bump twice.
        var oracle = new FakeOracle().Knows("GHSA-aaaa-bbbb-cccc", "1.2.0");
        var lookup = new FakeVersionLookup().Publishes("Pkg", "1.0.0", "1.2.0");
        var planner = new RemediationPlanner(oracle, lookup);

        var plan = await planner.PlanAsync(
            [Finding("Pkg", "1.0.0"), Finding("Pkg", "1.0.0"), Finding("Pkg", "1.0.0", "Worker.csproj")],
            allowMajor: false,
            includePrerelease: false
        );

        plan.Actions.Should().ContainSingle();
        plan.Actions[0].AffectedProjects.Should().BeEquivalentTo(["Api.csproj", "Worker.csproj"]);
    }

    [Fact]
    public async Task APackageIsTransitiveOnlyWhenEveryFindingSaysSo()
    {
        // One direct reference is enough to make the existing pin the thing to move; treating the
        // package as transitive would add a second entry instead of changing the one that exists.
        var oracle = new FakeOracle().Knows("GHSA-aaaa-bbbb-cccc", "1.2.0");
        var lookup = new FakeVersionLookup().Publishes("Pkg", "1.0.0", "1.2.0");
        var planner = new RemediationPlanner(oracle, lookup);

        var mixed = await planner.PlanAsync(
            [Finding("Pkg", "1.0.0", isTransitive: true), Finding("Pkg", "1.0.0", "Worker.csproj")],
            allowMajor: false,
            includePrerelease: false
        );
        var allTransitive = await planner.PlanAsync(
            [Finding("Pkg", "1.0.0", isTransitive: true)],
            allowMajor: false,
            includePrerelease: false
        );

        mixed.Actions[0].IsTransitive.Should().BeFalse();
        allTransitive.Actions[0].IsTransitive.Should().BeTrue();
    }

    [Fact]
    public async Task OnlyPackages_NarrowsThePlanRatherThanReportingTheRestAsUnfixed()
    {
        var oracle = new FakeOracle().Knows("GHSA-aaaa-bbbb-cccc", "1.2.0");
        var lookup = new FakeVersionLookup()
            .Publishes("Wanted", "1.0.0", "1.2.0")
            .Publishes("Ignored", "1.0.0", "1.2.0");
        var planner = new RemediationPlanner(oracle, lookup);

        var plan = await planner.PlanAsync(
            [Finding("Wanted", "1.0.0"), Finding("Ignored", "1.0.0")],
            allowMajor: false,
            includePrerelease: false,
            onlyPackages: ["Wanted"]
        );

        plan.Actions.Should().ContainSingle();
        plan.Actions[0].PackageName.Should().Be("Wanted");
    }

    [Fact]
    public async Task AVersionListTheFeedCouldNotServe_IsTreatedAsMissingDataNotAsNoFix()
    {
        var oracle = new FakeOracle().Knows("GHSA-aaaa-bbbb-cccc", "1.2.0");
        var planner = new RemediationPlanner(oracle, new FakeVersionLookup());

        var plan = await planner.PlanAsync(
            [Finding("Pkg", "1.0.0")],
            allowMajor: false,
            includePrerelease: false
        );

        plan.Actions[0].Outcome.Should().Be(RemediationOutcome.AdvisoryDataUnavailable);
        plan.HasUnavailableAdvisoryData.Should().BeTrue();
    }

    [Fact]
    public async Task TheWorstSeverityReportedForAPackage_IsTheOneCarried()
    {
        var oracle = new FakeOracle().Knows("GHSA-aaaa-bbbb-cccc", "1.2.0");
        var lookup = new FakeVersionLookup().Publishes("Pkg", "1.0.0", "1.2.0");
        var planner = new RemediationPlanner(oracle, lookup);

        var plan = await planner.PlanAsync(
            [Finding("Pkg", "1.0.0", severity: "Low"), Finding("Pkg", "1.0.0", severity: "Critical")],
            allowMajor: false,
            includePrerelease: false
        );

        plan.Actions[0].Severity.Should().Be("Critical");
    }
}
