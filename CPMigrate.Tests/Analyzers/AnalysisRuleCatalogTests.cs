using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

/// <summary>
/// Rule IDs are a public contract: they appear in SARIF <c>ruleId</c>, JSON <c>issueCode</c>, and
/// the published rule reference. These tests fail when a new issue code is added without the
/// documentation that consumers are pointed at.
/// </summary>
public class AnalysisRuleCatalogTests
{
    [Fact]
    public void Catalog_DescribesEveryIssueCode()
    {
        var described = AnalysisRuleCatalog.All.Select(rule => rule.Code);

        described.Should().BeEquivalentTo(Enum.GetValues<AnalysisIssueCode>());
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Get_ReturnsNonEmptyDocumentationForEveryCode(AnalysisIssueCode code)
    {
        var rule = AnalysisRuleCatalog.Get(code);

        rule.Code.Should().Be(code);
        rule.Id.Should().Be(code.ToString());
        rule.ShortDescription.Should().NotBeNullOrWhiteSpace();
        rule.FullDescription.Should().NotBeNullOrWhiteSpace();
        rule.Tags.Should().NotBeEmpty();
        rule.HelpUri.Should().StartWith(AnalysisRuleCatalog.DocumentationBaseUri + "#");
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void RuleReference_DocumentsEveryCode(AnalysisIssueCode code)
    {
        var reference = ReadRuleReference();

        reference
            .Should()
            .Contain(
                $"## {code}",
                $"docs/rules.md is the target of every {code} help URI and must have a matching section"
            );
    }

    public static TheoryData<AnalysisIssueCode> AllCodes()
    {
        var data = new TheoryData<AnalysisIssueCode>();
        foreach (var code in Enum.GetValues<AnalysisIssueCode>())
        {
            data.Add(code);
        }

        return data;
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root so the check works from any
    /// build output directory.
    /// </summary>
    private static string ReadRuleReference()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "rules.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate docs/rules.md from the test output directory."
        );
    }
}
