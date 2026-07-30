using System.Text;
using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Renders rule documentation for <c>--explain</c>.
///
/// A rule ID in a build log or a SARIF upload is the point at which someone needs to know what the
/// rule means, and that is exactly the moment they do not want to go looking for a docs site. The
/// catalog is already in the binary; this makes it reachable from the same terminal that produced the
/// finding.
/// </summary>
public static class RuleExplainer
{
    /// <summary>Argument that lists every rule rather than explaining one.</summary>
    public const string AllRules = "all";

    /// <summary>
    /// Explains a rule, or lists them all.
    /// </summary>
    /// <param name="query">A rule ID, or <see cref="AllRules"/>.</param>
    /// <returns>The text to print, and whether the query matched anything.</returns>
    public static (string Output, bool Found) Explain(string query)
    {
        if (
            string.IsNullOrWhiteSpace(query)
            || query.Equals(AllRules, StringComparison.OrdinalIgnoreCase)
        )
        {
            return (DescribeAll(), true);
        }

        var rule = AnalysisRuleCatalog.All.FirstOrDefault(candidate =>
            candidate.Id.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
        );

        return rule is null ? (DescribeUnknown(query), false) : (Describe(rule), true);
    }

    private static string Describe(AnalysisRule rule)
    {
        var text = new StringBuilder();

        text.AppendLine(rule.Id);
        text.AppendLine(new string('─', rule.Id.Length));
        text.AppendLine();
        text.AppendLine(rule.ShortDescription);
        text.AppendLine();
        text.AppendLine(Wrap(rule.FullDescription));
        text.AppendLine();
        text.AppendLine($"Tags: {string.Join(", ", rule.Tags)}");
        text.AppendLine($"Docs: {rule.HelpUri}");
        text.AppendLine();
        text.AppendLine(
            "This ID appears verbatim as `issueCode` in --output Json and `ruleId` in --output Sarif."
        );

        return text.ToString();
    }

    private static string DescribeAll()
    {
        var text = new StringBuilder();

        text.AppendLine("CPMigrate rules");
        text.AppendLine("───────────────");
        text.AppendLine();

        foreach (
            var rule in AnalysisRuleCatalog.All.Where(r => r.Code != AnalysisIssueCode.Unknown)
        )
        {
            text.AppendLine($"  {rule.Id}");
            text.AppendLine($"      {rule.ShortDescription}");
            text.AppendLine();
        }

        text.AppendLine("Explain one with: cpmigrate --explain <RuleId>");

        return text.ToString();
    }

    /// <summary>
    /// Reports an unrecognised ID, suggesting close matches. A bare "unknown rule" leaves someone who
    /// mistyped a capital letter with nothing to go on.
    /// </summary>
    private static string DescribeUnknown(string query)
    {
        var text = new StringBuilder();
        text.AppendLine($"Unknown rule: {query}");

        var suggestions = AnalysisRuleCatalog
            .All.Where(rule =>
                rule.Code != AnalysisIssueCode.Unknown
                && (
                    rule.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || query.Contains(rule.Id, StringComparison.OrdinalIgnoreCase)
                )
            )
            .Select(rule => rule.Id)
            .ToList();

        if (suggestions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Did you mean: {string.Join(", ", suggestions)}");
        }

        text.AppendLine();
        text.AppendLine("List every rule with: cpmigrate --explain all");

        return text.ToString();
    }

    /// <summary>
    /// Wraps prose to a readable width. The full descriptions are paragraphs, and a terminal that
    /// hard-wraps them at whatever the window happens to be makes them harder to read, not easier.
    /// </summary>
    private static string Wrap(string text, int width = 88)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return string.Join(Environment.NewLine, lines);
    }
}
