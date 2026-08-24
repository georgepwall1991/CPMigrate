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

        var example = GetExample(rule.Code);
        if (example is not null)
        {
            text.AppendLine("Example:");
            text.AppendLine($"  {example}");
            text.AppendLine();
        }

        var fix = GetFixHint(rule.Code);
        if (fix is not null)
        {
            text.AppendLine("How to fix:");
            text.AppendLine(Wrap(fix, 80));
            text.AppendLine();
        }

        text.AppendLine(
            "This ID appears verbatim as `issueCode` in --output Json and `ruleId` in --output Sarif."
        );

        return text.ToString();
    }

    private static string? GetExample(AnalysisIssueCode code) => code switch
    {
        AnalysisIssueCode.VersionInconsistency => "cpmigrate --analyze -s ./MySolution.sln",
        AnalysisIssueCode.DuplicatePackageCasing => "cpmigrate --analyze --fix -s ./MySolution.sln",
        AnalysisIssueCode.RedundantReference => "cpmigrate --analyze --fix -s ./MySolution.sln",
        AnalysisIssueCode.TransitiveConflict => "cpmigrate --analyze --transitive -s ./MySolution.sln",
        AnalysisIssueCode.SecurityVulnerability => "cpmigrate --analyze --audit --fail-on High",
        AnalysisIssueCode.OutdatedPackage => "cpmigrate --analyze --outdated",
        AnalysisIssueCode.DeprecatedPackage => "cpmigrate --analyze --deprecated",
        AnalysisIssueCode.FrameworkAlignment => "cpmigrate --analyze -s ./MySolution.sln",
        AnalysisIssueCode.LicenseRisk => "cpmigrate --analyze --licenses",
        AnalysisIssueCode.CpmNotEnabled => "cpmigrate -s ./MySolution.sln",
        AnalysisIssueCode.InlineVersionUnderCpm => "cpmigrate --analyze -s ./MySolution.sln",
        AnalysisIssueCode.OrphanedPackageVersion => "cpmigrate --analyze -s ./MySolution.sln",
        AnalysisIssueCode.EolTargetFramework => "cpmigrate --analyze -s ./MySolution.sln",
        _ => null,
    };

    private static string? GetFixHint(AnalysisIssueCode code) => code switch
    {
        AnalysisIssueCode.VersionInconsistency => "Run 'cpmigrate -s ./MySolution.sln' to centralize all versions in Directory.Packages.props, or 'cpmigrate --analyze --fix' to standardize versions in place.",
        AnalysisIssueCode.DuplicatePackageCasing => "Run 'cpmigrate --analyze --fix' to normalize package name casing to the most common variant.",
        AnalysisIssueCode.RedundantReference => "Run 'cpmigrate --analyze --fix' to remove duplicate PackageReference entries.",
        AnalysisIssueCode.TransitiveConflict => "Run 'cpmigrate --analyze --fix --transitive' to pin divergent transitive dependencies in Directory.Packages.props.",
        AnalysisIssueCode.SecurityVulnerability => "Run 'cpmigrate --update-packages' to update vulnerable packages, or 'cpmigrate --update-packages --bisect' to keep the largest green subset.",
        AnalysisIssueCode.OutdatedPackage => "Run 'cpmigrate --update-packages --dry-run' to preview available updates.",
        AnalysisIssueCode.DeprecatedPackage => "Check the package's NuGet page for a recommended replacement, then update Directory.Packages.props.",
        AnalysisIssueCode.CpmNotEnabled => "Run 'cpmigrate -s ./MySolution.sln' to generate Directory.Packages.props with ManagePackageVersionsCentrally enabled.",
        AnalysisIssueCode.InlineVersionUnderCpm => "Remove the Version attribute from the PackageReference — the version should come from Directory.Packages.props.",
        AnalysisIssueCode.OrphanedPackageVersion => "Remove the unused PackageVersion entry from Directory.Packages.props.",
        AnalysisIssueCode.LicenseRisk => "Review the package license. For copyleft licenses, consider a permissive alternative. For unverified (file or URL) licenses, read the terms on the package's NuGet page.",
        AnalysisIssueCode.EolTargetFramework => "Retarget the project to a supported LTS release (net8.0 or net10.0) in its TargetFramework or TargetFrameworks property, then update any packages that do not support the new target.",
        _ => null,
    };

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

        // Substring matching alone misses the case this exists for: an ordinary typo, where one
        // letter is wrong and neither string contains the other. Edit distance catches those.
        var suggestions = AnalysisRuleCatalog
            .All.Where(rule => rule.Code != AnalysisIssueCode.Unknown)
            .Select(rule => (rule.Id, Distance: Similarity(query.Trim(), rule.Id)))
            .Where(candidate => candidate.Distance <= MaxSuggestionDistance(query.Trim()))
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Id)
            .Take(3)
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
    /// How far a query may be from a rule ID and still be offered as a suggestion.
    ///
    /// Scaled to the query length rather than fixed: one edit is a plausible slip in a short word,
    /// while a 21-character rule ID can absorb two or three and still obviously be the same intent.
    /// Too generous and every typo suggests every rule, which is no more useful than silence.
    /// </summary>
    private static int MaxSuggestionDistance(string query)
    {
        return query.Length switch
        {
            <= 4 => 1,
            <= 10 => 2,
            _ => 4,
        };
    }

    /// <summary>
    /// Case-insensitive Levenshtein distance, with a substring match treated as very close — someone
    /// typing a fragment of a rule ID knows which rule they want, they just did not type all of it.
    /// </summary>
    private static int Similarity(string query, string ruleId)
    {
        if (
            ruleId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || query.Contains(ruleId, StringComparison.OrdinalIgnoreCase)
        )
        {
            return 0;
        }

        return Levenshtein(query.ToLowerInvariant(), ruleId.ToLowerInvariant());
    }

    /// <summary>
    /// Standard two-row Levenshtein distance. Two rows rather than a full matrix because the inputs
    /// are short and only the previous row is ever needed.
    /// </summary>
    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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
