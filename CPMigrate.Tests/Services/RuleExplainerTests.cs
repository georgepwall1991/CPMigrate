using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// A rule ID in a build log is exactly where someone needs to know what the rule means — and exactly
/// where they will not go looking for a docs site. These tests pin that every rule is explainable and
/// that a mistyped ID leaves the user with something to act on.
/// </summary>
public class RuleExplainerTests
{
    [Theory]
    [MemberData(nameof(RealRules))]
    public void Explain_EveryRule_IsExplainableAndSaysWhereTheIdAppears(AnalysisIssueCode code)
    {
        var rule = AnalysisRuleCatalog.Get(code);

        var (output, found) = RuleExplainer.Explain(rule.Id);

        found.Should().BeTrue();
        output.Should().Contain(rule.Id);
        output.Should().Contain(rule.ShortDescription);
        output.Should().Contain(rule.HelpUri);
        // The link between a log line and this text is the ID, so the output has to name it.
        output.Should().Contain("issueCode").And.Contain("ruleId");
    }

    [Fact]
    public void Explain_IsCaseInsensitive()
    {
        // Nobody types VersionInconsistency correctly from memory every time.
        RuleExplainer.Explain("versioninconsistency").Found.Should().BeTrue();
        RuleExplainer.Explain("VERSIONINCONSISTENCY").Found.Should().BeTrue();
        RuleExplainer.Explain("  VersionInconsistency  ").Found.Should().BeTrue();
    }

    [Fact]
    public void Explain_All_ListsEveryRealRule()
    {
        var (output, found) = RuleExplainer.Explain("all");

        found.Should().BeTrue();
        foreach (
            var rule in AnalysisRuleCatalog.All.Where(r => r.Code != AnalysisIssueCode.Unknown)
        )
        {
            output.Should().Contain(rule.Id);
        }

        output.Should().NotContain("Unknown", "the fallback code is not a rule anyone can act on");
    }

    [Fact]
    public void Explain_NoArgument_ListsEveryRule()
    {
        RuleExplainer.Explain(string.Empty).Found.Should().BeTrue();
    }

    [Fact]
    public void Explain_UnknownRule_IsNotFoundAndOffersAWayForward()
    {
        var (output, found) = RuleExplainer.Explain("NoSuchRule");

        found.Should().BeFalse("the caller exits non-zero so a typo in CI is visible");
        output.Should().Contain("Unknown rule: NoSuchRule");
        output.Should().Contain("--explain all");
    }

    [Fact]
    public void Explain_NearMiss_SuggestsTheRealRule()
    {
        // A bare "unknown rule" leaves someone who mistyped one word with nothing to go on.
        var (output, _) = RuleExplainer.Explain("Inconsistency");

        output.Should().Contain("Did you mean").And.Contain("VersionInconsistency");
    }

    [Theory]
    [InlineData("VersionInconsistncy", "VersionInconsistency")]
    [InlineData("VersionInconsistancy", "VersionInconsistency")]
    [InlineData("SecurityVulnerabilty", "SecurityVulnerability")]
    [InlineData("OrphanedPackageVerson", "OrphanedPackageVersion")]
    [InlineData("CpmNotEnabld", "CpmNotEnabled")]
    public void Explain_OrdinaryTypo_SuggestsTheRealRule(string typo, string expected)
    {
        // The case the feature exists for: one letter wrong, and neither string contains the other,
        // so substring matching alone would say nothing at all.
        var (output, found) = RuleExplainer.Explain(typo);

        found.Should().BeFalse();
        output.Should().Contain("Did you mean").And.Contain(expected);
    }

    [Fact]
    public void Explain_SomethingUnrelated_SuggestsNothingRatherThanEverything()
    {
        // A suggestion list containing every rule is no more useful than silence.
        var (output, _) = RuleExplainer.Explain("zzzzzzzzzzzzzzzz");

        output.Should().NotContain("Did you mean");
    }

    [Fact]
    public void Explain_WrapsProseRatherThanEmittingOneLongLine()
    {
        var (output, _) = RuleExplainer.Explain("SecurityVulnerability");

        var longest = output.Split(Environment.NewLine).Max(line => line.Length);
        longest.Should().BeLessThan(120, "unwrapped paragraphs are harder to read, not easier");
    }

    public static TheoryData<AnalysisIssueCode> RealRules()
    {
        var data = new TheoryData<AnalysisIssueCode>();
        foreach (
            var code in Enum.GetValues<AnalysisIssueCode>()
                .Where(c => c != AnalysisIssueCode.Unknown)
        )
        {
            data.Add(code);
        }

        return data;
    }
}
