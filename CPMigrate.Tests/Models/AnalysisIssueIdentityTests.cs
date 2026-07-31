using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Models;

/// <summary>
/// A fingerprint decides whether two findings are "the same one" — for SARIF tracking and for
/// baseline suppression. Two distinct findings sharing one is the quiet failure: a baseline records
/// one and suppresses both, so a finding nobody accepted disappears.
/// </summary>
public class AnalysisIssueIdentityTests
{
    [Fact]
    public void Compute_TwoSpecificationsOfOnePackage_AreDistinctFindings()
    {
        // FloatingVersion reports one finding per version specification, and a central pin names no
        // project at all — so without the specification these two collapse into one identity.
        var wildcard = FloatingIssue("4.*");
        var range = FloatingIssue("[4.0.0,)");

        AnalysisIssueIdentity
            .Compute(wildcard)
            .Should()
            .NotBe(AnalysisIssueIdentity.Compute(range));
    }

    [Fact]
    public void Compute_TheSameSpecification_IsTheSameFindingAcrossRuns()
    {
        AnalysisIssueIdentity
            .Compute(FloatingIssue("4.*"))
            .Should()
            .Be(AnalysisIssueIdentity.Compute(FloatingIssue("4.*")));
    }

    [Fact]
    public void Compute_SpecificationCasingAndPadding_DoNotChangeIdentity()
    {
        AnalysisIssueIdentity
            .Compute(FloatingIssue("4.*"))
            .Should()
            .Be(AnalysisIssueIdentity.Compute(FloatingIssue("  4.*  ")));
    }

    [Fact]
    public void Compute_AFindingWithoutASpecification_IsUnchangedByTheDiscriminator()
    {
        // The identity of every pre-existing rule has to be exactly what it was, or committed
        // baselines stop matching and accepted debt starts failing builds. Pinned by value, because
        // recomputing it from the same code would agree with itself however it changed.
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Found 2 different versions",
            new[] { "src/Api/Api.csproj" },
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        );

        AnalysisIssueIdentity.Compute(issue).Should().Be("5f923aa4b34738da3ae4a46ba6ae1fad");
    }

    [Fact]
    public void Compute_MetadataOtherThanTheSpecification_DoesNotChangeIdentity()
    {
        // Only the specification participates. Folding in arbitrary metadata would make the
        // identity depend on fields that describe a finding rather than identify it.
        var withoutKind = FloatingIssue("4.*", includeKind: false);
        var withKind = FloatingIssue("4.*", includeKind: true);

        AnalysisIssueIdentity
            .Compute(withoutKind)
            .Should()
            .Be(AnalysisIssueIdentity.Compute(withKind));
    }

    private static AnalysisIssue FloatingIssue(string specification, bool includeKind = true)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["versionSpecification"] = specification,
        };

        if (includeKind)
        {
            metadata["kind"] = "wildcard";
        }

        return new AnalysisIssue(
            "Serilog",
            $"Version '{specification}' floats",
            Array.Empty<string>(),
            AnalysisIssueCode.FloatingVersion,
            AnalysisSeverity.Moderate,
            Metadata: metadata
        );
    }
}
