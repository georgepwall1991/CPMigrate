using CPMigrate.Services.Remediation;
using FluentAssertions;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services.Remediation;

/// <summary>
/// Guards the choice of fix version.
///
/// This is the decision that distinguishes remediation from an update, so the tests that matter most
/// are the ones asserting it picks the <em>lowest</em> clear version rather than the newest. Getting
/// that wrong does not fail loudly: it produces a working, green, much larger diff that quietly turns
/// a security patch into a feature upgrade.
/// </summary>
public class SafeVersionResolverTests
{
    private static AdvisoryRecord FixedIn(string fixedVersion, string introduced = "0.0.0")
    {
        return new AdvisoryRecord(
            "GHSA-test",
            ["CVE-0000-0000"],
            "HIGH",
            [
                new AdvisoryVersionRange(
                    [
                        new AdvisoryBoundary(NuGetVersion.Parse(introduced), AdvisoryBoundaryKind.Introduced),
                        new AdvisoryBoundary(NuGetVersion.Parse(fixedVersion), AdvisoryBoundaryKind.Fixed),
                    ]
                ),
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );
    }

    private static IReadOnlyList<NuGetVersion> Versions(params string[] versions)
    {
        return versions.Select(NuGetVersion.Parse).ToList();
    }

    [Fact]
    public void TheLowestClearVersion_IsChosen_NotTheNewest()
    {
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("1.0.0"),
            [FixedIn("1.2.0")],
            Versions("1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.9.9"),
            includePrerelease: false
        );

        outcome.Status.Should().Be(SafeVersionStatus.InMajor);
        outcome.Version!.ToNormalizedString().Should().Be("1.2.0");
    }

    [Fact]
    public void EveryAdvisoryHasToBeCleared_NotJustTheWorstOne()
    {
        // A package with two advisories whose fixes land in different versions is the case where
        // clearing only one still leaves a reported CVE in place while the run reports success.
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("1.0.0"),
            [FixedIn("1.2.0"), FixedIn("1.5.0")],
            Versions("1.0.0", "1.2.0", "1.4.0", "1.5.0", "1.6.0"),
            includePrerelease: false
        );

        outcome.Version!.ToNormalizedString().Should().Be("1.5.0");
    }

    [Fact]
    public void AFixOnlyInTheNextMajor_IsReportedAsCrossMajorRatherThanAppliedQuietly()
    {
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("9.0.1"),
            [FixedIn("13.0.1")],
            Versions("9.0.1", "10.0.3", "12.0.3", "13.0.1", "13.0.4"),
            includePrerelease: false
        );

        outcome.Status.Should().Be(SafeVersionStatus.CrossMajor);
        outcome.Version!.ToNormalizedString().Should().Be("13.0.1");
    }

    [Fact]
    public void AnInMajorFix_IsPreferredEvenWhenAHigherMajorIsAlsoClear()
    {
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("1.0.0"),
            [FixedIn("1.4.0")],
            Versions("1.0.0", "1.4.0", "2.0.0", "3.0.0"),
            includePrerelease: false
        );

        outcome.Status.Should().Be(SafeVersionStatus.InMajor);
        outcome.Version!.ToNormalizedString().Should().Be("1.4.0");
    }

    [Fact]
    public void APrereleaseOfTheFixedVersion_IsStillVulnerable()
    {
        // 2.0.0-beta1 sorts below 2.0.0, so it predates the fix and the advisory still covers it.
        // Worth pinning: "contains the fix version's number" is an easy thing to mistake for "is
        // fixed", and that mistake ships an exposed package under a green run.
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("1.0.0"),
            [FixedIn("2.0.0")],
            Versions("1.0.0", "2.0.0-beta1", "2.0.0"),
            includePrerelease: true
        );

        outcome.Version!.ToNormalizedString().Should().Be("2.0.0");
    }

    [Fact]
    public void PrereleaseVersions_AreOnlyChosenWhenAskedFor()
    {
        var advisories = new[] { FixedIn("2.0.0") };
        var available = Versions("1.0.0", "2.1.0-beta1", "3.0.0");

        SafeVersionResolver
            .Resolve(NuGetVersion.Parse("1.0.0"), advisories, available, includePrerelease: false)
            .Version!.ToNormalizedString()
            .Should()
            .Be("3.0.0", "the only lower clear version is a pre-release the caller did not opt into");

        SafeVersionResolver
            .Resolve(NuGetVersion.Parse("1.0.0"), advisories, available, includePrerelease: true)
            .Version!.ToNormalizedString()
            .Should()
            .Be("2.1.0-beta1");
    }

    [Fact]
    public void ADowngradeThatPredatesTheFlaw_IsNeverOffered()
    {
        // 0.9.0 is outside the advisory's range, but moving backwards is not a fix anyone wants
        // applied automatically — it undoes whatever the intervening versions changed.
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("1.5.0"),
            [FixedIn("2.0.0", introduced: "1.0.0")],
            Versions("0.9.0", "1.5.0"),
            includePrerelease: false
        );

        outcome.Status.Should().Be(SafeVersionStatus.NoFixAvailable);
        outcome.Version.Should().BeNull();
    }

    [Fact]
    public void NoPublishedVersionClearingTheAdvisory_SaysSoRatherThanPickingTheNewest()
    {
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("1.0.0"),
            [FixedIn("9.9.9")],
            Versions("1.0.0", "1.1.0", "1.2.0"),
            includePrerelease: false
        );

        outcome.Status.Should().Be(SafeVersionStatus.NoFixAvailable);
        outcome.Version.Should().BeNull();
    }

    [Fact]
    public void AnAdvisoryThatDoesNotDescribeTheResolvedVersion_IsReportedRatherThanDismissed()
    {
        // The SDK says this version is exposed; the advisory data says it is not. Answering "already
        // safe" would dismiss a real finding on the strength of data that just contradicted itself.
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("5.0.0"),
            [FixedIn("1.2.0")],
            Versions("5.0.0", "5.1.0"),
            includePrerelease: false
        );

        outcome.Status.Should().Be(SafeVersionStatus.AdvisoryDoesNotCoverResolvedVersion);
        outcome.Version.Should().BeNull();
    }

    [Fact]
    public void NoAdvisories_ProducesNoTarget()
    {
        var outcome = SafeVersionResolver.Resolve(
            NuGetVersion.Parse("1.0.0"),
            [],
            Versions("1.0.0", "2.0.0"),
            includePrerelease: false
        );

        outcome.Status.Should().Be(SafeVersionStatus.NoFixAvailable);
    }
}
