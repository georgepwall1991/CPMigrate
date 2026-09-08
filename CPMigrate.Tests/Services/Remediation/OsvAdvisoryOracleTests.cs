using System.Text.Json;
using CPMigrate.Services.Remediation;
using FluentAssertions;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services.Remediation;

/// <summary>
/// Guards the parse of an OSV advisory document.
///
/// The payload here is a real capture from <c>GET https://api.osv.dev/v1/vulns/GHSA-5crp-9r3c-p9vr</c>
/// with only the long prose fields removed — the same discipline
/// <see cref="RecordedFeedOutputTests"/> applies to the SDK's output, and for the same reason: a
/// hand-written fixture proves the parser handles the JSON the test author imagined, which is
/// exactly the shape that was never in doubt. Everything remediation decides rests on reading these
/// ranges correctly, and reading them wrongly produces a confident, wrong "safe" version.
/// </summary>
public class OsvAdvisoryOracleTests
{
    private const string RecordedNewtonsoftAdvisory = """
    {
      "id": "GHSA-5crp-9r3c-p9vr",
      "summary": "Improper Handling of Exceptional Conditions in Newtonsoft.Json",
      "aliases": [ "CVE-2024-21907" ],
      "modified": "2026-07-08T06:53:11.583483075Z",
      "published": "2022-06-22T15:08:47Z",
      "database_specific": {
        "cwe_ids": [ "CWE-755" ],
        "github_reviewed": true,
        "severity": "HIGH"
      },
      "affected": [
        {
          "package": {
            "name": "Newtonsoft.Json",
            "ecosystem": "NuGet",
            "purl": "pkg:nuget/Newtonsoft.Json"
          },
          "ranges": [
            {
              "type": "ECOSYSTEM",
              "events": [ { "introduced": "0" }, { "fixed": "13.0.1" } ]
            }
          ],
          "versions": [ "10.0.1", "10.0.1-beta1", "10.0.2", "10.0.3" ]
        }
      ],
      "schema_version": "1.7.3"
    }
    """;

    private static AdvisoryRecord? Parse(string json, string packageId)
    {
        using var document = JsonDocument.Parse(json);
        return OsvAdvisoryOracle.ParseAdvisory(document.RootElement, "GHSA-5crp-9r3c-p9vr", packageId);
    }

    [Fact]
    public void RecordedAdvisory_YieldsTheRangeThatDecidesEveryFixVersion()
    {
        var record = Parse(RecordedNewtonsoftAdvisory, "Newtonsoft.Json");

        record.Should().NotBeNull();
        record!.Id.Should().Be("GHSA-5crp-9r3c-p9vr");
        record.Severity.Should().Be("HIGH");

        record.Affects(NuGetVersion.Parse("9.0.1")).Should().BeTrue();
        record.Affects(NuGetVersion.Parse("12.0.3")).Should().BeTrue();
        record.Affects(NuGetVersion.Parse("13.0.1")).Should().BeFalse("13.0.1 is the fixed version");
        record.Affects(NuGetVersion.Parse("13.0.4")).Should().BeFalse();
    }

    [Fact]
    public void RecordedAdvisory_CarriesTheCveTheSdkNeverReports()
    {
        // dotnet list package --vulnerable emits an advisory URL and nothing else. The CVE number is
        // the identifier that reaches a compliance report, and this is the only place it comes from.
        var record = Parse(RecordedNewtonsoftAdvisory, "Newtonsoft.Json");

        record!.Aliases.Should().Contain("CVE-2024-21907");
        record.DisplayId.Should().Be("CVE-2024-21907");
    }

    [Fact]
    public void AnAdvisoryAboutAnotherPackage_IsNotReadAsThisOne()
    {
        // An advisory can list several packages. Folding a sibling's ranges in would compute a fix
        // version out of versions this package never published.
        var record = Parse(RecordedNewtonsoftAdvisory, "Some.Other.Package");

        record.Should().BeNull();
    }

    [Fact]
    public void ANonNuGetEcosystem_IsIgnoredEvenWhenTheNameMatches()
    {
        const string json = """
        {
          "id": "GHSA-test",
          "affected": [
            {
              "package": { "name": "Shared.Name", "ecosystem": "npm" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "2.0.0" } ] } ]
            }
          ]
        }
        """;

        Parse(json, "Shared.Name").Should().BeNull();
    }

    [Fact]
    public void ARangeThatReopens_LeavesTheGapBetweenFixAndReintroductionSafe()
    {
        // A flaw fixed in 2.0.0 and reintroduced in 3.0.0 is one range with four events. Collapsing
        // it into a single interval would report 2.x as vulnerable and push a user off a version
        // that is fine.
        const string json = """
        {
          "id": "GHSA-reopen",
          "affected": [
            {
              "package": { "name": "Pkg", "ecosystem": "NuGet" },
              "ranges": [
                {
                  "type": "ECOSYSTEM",
                  "events": [
                    { "introduced": "1.0.0" },
                    { "fixed": "2.0.0" },
                    { "introduced": "3.0.0" },
                    { "fixed": "3.5.0" }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var record = Parse(json, "Pkg")!;

        record.Affects(NuGetVersion.Parse("1.5.0")).Should().BeTrue();
        record.Affects(NuGetVersion.Parse("2.4.0")).Should().BeFalse("fixed in 2.0.0, not yet reintroduced");
        record.Affects(NuGetVersion.Parse("3.1.0")).Should().BeTrue("reintroduced in 3.0.0");
        record.Affects(NuGetVersion.Parse("3.5.0")).Should().BeFalse();
    }

    [Fact]
    public void LastAffected_IsInclusiveWhereFixedIsExclusive()
    {
        const string json = """
        {
          "id": "GHSA-last",
          "affected": [
            {
              "package": { "name": "Pkg", "ecosystem": "NuGet" },
              "ranges": [
                { "type": "ECOSYSTEM", "events": [ { "introduced": "1.0.0" }, { "last_affected": "1.4.2" } ] }
              ]
            }
          ]
        }
        """;

        var record = Parse(json, "Pkg")!;

        record.Affects(NuGetVersion.Parse("1.4.2")).Should().BeTrue("last_affected names a version that is still affected");
        record.Affects(NuGetVersion.Parse("1.4.3")).Should().BeFalse();
    }

    [Fact]
    public void AVersionListedOutright_IsAffectedEvenWithoutARange()
    {
        const string json = """
        {
          "id": "GHSA-explicit",
          "affected": [
            {
              "package": { "name": "Pkg", "ecosystem": "NuGet" },
              "versions": [ "1.2.3", "1.2.4" ]
            }
          ]
        }
        """;

        var record = Parse(json, "Pkg")!;

        record.Affects(NuGetVersion.Parse("1.2.3")).Should().BeTrue();
        record.Affects(NuGetVersion.Parse("1.2.5")).Should().BeFalse();
    }

    [Fact]
    public void AnAdvisorySayingNothingAboutVersions_IsNoAnswerRatherThanAnAllClear()
    {
        // No ranges and no version list cannot prove any version safe. Returning an empty record
        // would let the planner treat every version as clear and "remediate" to the next patch.
        const string json = """
        {
          "id": "GHSA-empty",
          "affected": [ { "package": { "name": "Pkg", "ecosystem": "NuGet" } } ]
        }
        """;

        Parse(json, "Pkg").Should().BeNull();
    }

    [Fact]
    public void AGitRangeAlongsideAnEcosystemRange_IsSkippedRatherThanReadAsVersions()
    {
        // GIT boundaries are commit hashes with no version ordering. Reading them would drop the
        // unparseable "fixed" event, leaving a range that opens and never closes — so every version
        // reads as vulnerable and a package with a published fix is reported unfixable.
        const string json = """
        {
          "id": "GHSA-git",
          "affected": [
            {
              "package": { "name": "Pkg", "ecosystem": "NuGet" },
              "ranges": [
                {
                  "type": "GIT",
                  "repo": "https://github.com/example/pkg",
                  "events": [ { "introduced": "0" }, { "fixed": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2" } ]
                },
                {
                  "type": "ECOSYSTEM",
                  "events": [ { "introduced": "0" }, { "fixed": "2.0.0" } ]
                }
              ]
            }
          ]
        }
        """;

        var record = Parse(json, "Pkg")!;

        record.Affects(NuGetVersion.Parse("1.9.0")).Should().BeTrue();
        record.Affects(NuGetVersion.Parse("2.0.0")).Should().BeFalse("the ECOSYSTEM range is the one that describes NuGet");
    }

    [Fact]
    public void AnUnparseableBoundaryInsideAVersionRange_RefusesTheAdvisoryRatherThanGuessing()
    {
        // Dropping the bad event would leave a half-open range. Refusing sends this to the planner
        // as "no usable data", which fails the run loudly instead of computing a wrong fix version.
        const string json = """
        {
          "id": "GHSA-bad",
          "affected": [
            {
              "package": { "name": "Pkg", "ecosystem": "NuGet" },
              "ranges": [
                { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "not-a-version" } ] }
              ]
            }
          ]
        }
        """;

        Parse(json, "Pkg").Should().BeNull();
    }

    [Fact]
    public void AnExplicitVersion_MatchesTheSameReleaseSpelledDifferently()
    {
        // OSV spells versions as the feed published them; candidates come from NuGet's index. A
        // string compare would miss 4.5 vs 4.5.0, and the miss offers an affected version as the fix.
        const string json = """
        {
          "id": "GHSA-spelling",
          "affected": [
            {
              "package": { "name": "Pkg", "ecosystem": "NuGet" },
              "versions": [ "4.5" ]
            }
          ]
        }
        """;

        var record = Parse(json, "Pkg")!;

        record.Affects(NuGetVersion.Parse("4.5.0")).Should().BeTrue();
        record.Affects(NuGetVersion.Parse("4.6.0")).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://github.com/advisories/GHSA-5crp-9r3c-p9vr", "GHSA-5crp-9r3c-p9vr")]
    [InlineData("https://github.com/advisories/GHSA-5crp-9r3c-p9vr/", "GHSA-5crp-9r3c-p9vr")]
    [InlineData("GHSA-5crp-9r3c-p9vr", "GHSA-5crp-9r3c-p9vr")]
    [InlineData("CVE-2024-21907", "CVE-2024-21907")]
    public void AnAdvisoryUrl_ReducesToTheIdentifierOsvAnswersFor(string input, string expected)
    {
        OsvAdvisoryOracle.ExtractAdvisoryId(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/advisories")]
    [InlineData("https://example.com/a?q=1")]
    public void SomethingThatIsNotAnAdvisoryId_YieldsNothingRatherThanAGuess(string input)
    {
        OsvAdvisoryOracle.ExtractAdvisoryId(input).Should().BeEmpty();
    }
}
