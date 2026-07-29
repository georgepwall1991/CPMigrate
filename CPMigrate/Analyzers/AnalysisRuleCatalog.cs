using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Documentation for a single analyzer rule, keyed by <see cref="AnalysisIssueCode"/>.
/// </summary>
/// <param name="Code">The issue code this rule describes.</param>
/// <param name="ShortDescription">One-line summary shown in rule listings.</param>
/// <param name="FullDescription">Explanation of why the finding matters and how to resolve it.</param>
/// <param name="Tags">Classification tags surfaced to SARIF consumers.</param>
public record AnalysisRule(
    AnalysisIssueCode Code,
    string ShortDescription,
    string FullDescription,
    IReadOnlyList<string> Tags
)
{
    /// <summary>
    /// The stable rule identifier used in SARIF output and JSON <c>issueCode</c> fields.
    /// </summary>
    public string Id => Code.ToString();

    /// <summary>
    /// Anchor into the published rule documentation.
    /// </summary>
    public string HelpUri =>
        $"{AnalysisRuleCatalog.DocumentationBaseUri}#{Code.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Central description of every analyzer rule CPMigrate can report. Keeping the text in one
/// place lets SARIF, JSON, and the docs stay consistent as rules are added.
/// </summary>
public static class AnalysisRuleCatalog
{
    /// <summary>
    /// Canonical location of the human-readable rule reference.
    /// </summary>
#pragma warning disable S1075 // URIs should not be hardcoded - the published rule reference lives at a fixed location
    public const string DocumentationBaseUri =
        "https://github.com/georgepwall1991/CPMigrate/blob/main/docs/rules.md";
#pragma warning restore S1075

    private static readonly IReadOnlyDictionary<AnalysisIssueCode, AnalysisRule> Rules =
        new AnalysisRule[]
        {
            new(
                AnalysisIssueCode.VersionInconsistency,
                "The same package is referenced at different versions across projects.",
                "Divergent versions of one package in a single solution produce assembly binding surprises and "
                    + "make upgrades unpredictable. Centralize the package in Directory.Packages.props so every project "
                    + "resolves the same version.",
                new[] { "dependencies", "consistency" }
            ),
            new(
                AnalysisIssueCode.DuplicatePackageCasing,
                "One package is referenced under multiple casings.",
                "NuGet package IDs are case-insensitive, so 'newtonsoft.json' and 'Newtonsoft.Json' are the same "
                    + "package. Mixed casing defeats deduplication and can produce duplicate PackageVersion entries under "
                    + "central package management. Settle on the canonical casing published on nuget.org.",
                new[] { "dependencies", "consistency" }
            ),
            new(
                AnalysisIssueCode.RedundantReference,
                "A project declares the same PackageReference more than once.",
                "Repeated PackageReference items for one package are ignored by NuGet at best and produce "
                    + "conflicting versions at worst. Remove the duplicates and keep a single declaration.",
                new[] { "dependencies", "maintainability" }
            ),
            new(
                AnalysisIssueCode.TransitiveConflict,
                "Projects resolve conflicting versions of a shared transitive dependency.",
                "When two projects pull different versions of the same transitive package, the version that wins at "
                    + "runtime depends on build order and restore graph shape. Pin the package explicitly so the resolved "
                    + "version is deliberate.",
                new[] { "dependencies", "reliability" }
            ),
            new(
                AnalysisIssueCode.SecurityVulnerability,
                "A referenced package has a known security advisory.",
                "NuGet audit reported a published advisory affecting this package. Upgrade to the fixed version, or "
                    + "if the package is transitive, pin a patched version explicitly.",
                new[] { "security", "dependencies" }
            ),
            new(
                AnalysisIssueCode.RedundantDirectReference,
                "A package is referenced directly even though it already arrives transitively.",
                "Direct references that duplicate a transitive dependency add maintenance cost and can pin an older "
                    + "version than the graph would otherwise resolve. Remove the direct reference unless the project "
                    + "genuinely needs to control that version.",
                new[] { "dependencies", "maintainability" }
            ),
            new(
                AnalysisIssueCode.FrameworkAlignment,
                "Projects in the solution target different frameworks.",
                "Mixed target frameworks constrain which package versions can be shared and often surface as "
                    + "restore failures during a central package management migration. Align the frameworks, or confirm "
                    + "the split is intentional.",
                new[] { "dependencies", "consistency" }
            ),
            new(
                AnalysisIssueCode.OutdatedPackage,
                "A newer version of the package is published.",
                "The referenced version is behind the latest release on the configured feed. Review the changelog "
                    + "and update, or pin deliberately if the newer version is not wanted yet.",
                new[] { "dependencies", "maintenance" }
            ),
            new(
                AnalysisIssueCode.DeprecatedPackage,
                "The package is marked deprecated by its author.",
                "Deprecated packages stop receiving fixes, including security fixes. Migrate to the suggested "
                    + "alternative, or vendor the functionality if no replacement exists.",
                new[] { "dependencies", "maintenance" }
            ),
            new(
                AnalysisIssueCode.Unknown,
                "An analyzer reported a finding without a specific rule code.",
                "This is a fallback used when an analyzer does not classify its finding. Treat the message text as "
                    + "the source of truth.",
                new[] { "dependencies" }
            ),
        }.ToDictionary(rule => rule.Code);

    /// <summary>
    /// All known rules, ordered by issue code.
    /// </summary>
    public static IReadOnlyList<AnalysisRule> All { get; } =
        Rules.Values.OrderBy(rule => rule.Code).ToList();

    /// <summary>
    /// Looks up the documentation for an issue code, falling back to the
    /// <see cref="AnalysisIssueCode.Unknown"/> entry for codes added without catalog text.
    /// </summary>
    public static AnalysisRule Get(AnalysisIssueCode code)
    {
        return Rules.TryGetValue(code, out var rule) ? rule : Rules[AnalysisIssueCode.Unknown];
    }
}
