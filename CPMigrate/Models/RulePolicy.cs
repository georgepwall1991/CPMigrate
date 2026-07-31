using System.Globalization;

namespace CPMigrate.Models;

/// <summary>
/// A team's decision about which analyzer rules apply and how hard each one bites.
///
/// <para>
/// Two settings already narrow what fails a build, and neither covers this. <c>--fail-on</c> is a
/// single global threshold, so silencing one noisy rule means lowering the gate for every rule.
/// A baseline accepts findings that exist <em>today</em>, so it cannot express "we do not care about
/// this rule at all" — the next occurrence still lands.
/// </para>
///
/// <para>
/// Disabling removes a rule's findings from the report entirely rather than marking them suppressed.
/// That is the deliberate difference from a baseline: accepted debt stays visible because the point
/// is to pay it down, while a rule a team has switched off should not keep appearing in reports it
/// has been excluded from.
/// </para>
/// </summary>
public sealed class RulePolicy
{
    /// <summary>The value that turns a rule off, as opposed to re-grading it.</summary>
    public const string DisableKeyword = "none";

    private readonly IReadOnlyDictionary<AnalysisIssueCode, AnalysisSeverity?> _entries;

    private RulePolicy(IReadOnlyDictionary<AnalysisIssueCode, AnalysisSeverity?> entries)
    {
        _entries = entries;
    }

    /// <summary>A policy that changes nothing — every rule keeps the severity its analyzer assigned.</summary>
    public static RulePolicy Empty { get; } =
        new(new Dictionary<AnalysisIssueCode, AnalysisSeverity?>());

    /// <summary>True when the policy has nothing to apply.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Rules that report nothing, ordered by issue code.</summary>
    public IReadOnlyList<AnalysisIssueCode> DisabledRules { get; private init; } = [];

    /// <summary>Rules whose findings are re-graded, and the severity they are re-graded to.</summary>
    public IReadOnlyDictionary<AnalysisIssueCode, AnalysisSeverity> SeverityOverrides
    {
        get;
        private init;
    } = new Dictionary<AnalysisIssueCode, AnalysisSeverity>();

    /// <summary>
    /// <see cref="DisabledRules"/> as rule IDs for the JSON payload, or null when none were
    /// disabled. Null rather than an empty array because the field is omitted entirely in that
    /// case: an empty array would read as "a policy ran and disabled nothing", which is a different
    /// statement from "no policy".
    /// </summary>
    public IReadOnlyList<string>? ReportedDisabledRules()
    {
        return DisabledRules.Count == 0
            ? null
            : DisabledRules.Select(code => code.ToString()).ToList();
    }

    /// <summary>
    /// <see cref="SeverityOverrides"/> as rule IDs for the JSON payload, or null when none were
    /// overridden.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ReportedSeverityOverrides()
    {
        return SeverityOverrides.Count == 0
            ? null
            : SeverityOverrides.ToDictionary(
                entry => entry.Key.ToString(),
                entry => entry.Value.ToString()
            );
    }

    /// <summary>
    /// Splits a <c>--rules</c> spec into its individual <c>Rule=Value</c> entries. Empty segments are
    /// dropped so a trailing comma is not an error, but anything else is left for
    /// <see cref="Parse"/> to reject.
    /// </summary>
    /// <param name="spec">A comma-separated spec, or null.</param>
    public static IReadOnlyList<string> SplitSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return [];
        }

        return spec.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
    }

    /// <summary>
    /// Parses <c>Rule=Value</c> entries into a policy. The parse is strict on purpose: a misspelled
    /// rule ID that parsed to "no change" would leave the rule armed while looking exactly like a
    /// working configuration, which is the failure this whole feature exists to avoid on the other
    /// side of the gate.
    /// </summary>
    /// <param name="entries">Entries from <c>--rules</c> or from the config file's <c>rules</c> map.</param>
    /// <returns>The policy, or a message describing the first entry that could not be understood.</returns>
    public static (RulePolicy? Policy, string? Error) Parse(IEnumerable<string>? entries)
    {
        var parsed = new Dictionary<AnalysisIssueCode, AnalysisSeverity?>();

        foreach (var entry in entries ?? [])
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return (
                    null,
                    $"Invalid rule policy '{entry}': expected Rule=Severity, for example "
                        + $"'OutdatedPackage={DisableKeyword}' or 'LicenseRisk=Critical'."
                );
            }

            var ruleText = entry[..separator].Trim();
            var valueText = entry[(separator + 1)..].Trim();

            if (
                !Enum.TryParse<AnalysisIssueCode>(ruleText, ignoreCase: true, out var code)
                || !Enum.IsDefined(code)
            )
            {
                return (
                    null,
                    $"Unknown rule '{ruleText}'. Run 'cpmigrate --explain all' to list every rule ID."
                );
            }

            if (string.Equals(valueText, DisableKeyword, StringComparison.OrdinalIgnoreCase))
            {
                parsed[code] = null;
                continue;
            }

            if (
                !Enum.TryParse<AnalysisSeverity>(valueText, ignoreCase: true, out var severity)
                || !Enum.IsDefined(severity)
            )
            {
                var accepted = string.Join(
                    ", ",
                    Enum.GetNames<AnalysisSeverity>().Append(DisableKeyword)
                );
                return (
                    null,
                    $"Unknown severity '{valueText}' for rule '{ruleText}'. Accepted values: {accepted}."
                );
            }

            parsed[code] = severity;
        }

        return (
            new RulePolicy(parsed)
            {
                DisabledRules = parsed
                    .Where(entry => entry.Value is null)
                    .Select(entry => entry.Key)
                    .OrderBy(code => code)
                    .ToList(),
                SeverityOverrides = parsed
                    .Where(entry => entry.Value is not null)
                    .OrderBy(entry => entry.Key)
                    .ToDictionary(entry => entry.Key, entry => entry.Value!.Value),
            },
            null
        );
    }

    /// <summary>
    /// Applies the policy to a report: disabled rules lose their findings, overridden rules keep
    /// theirs at the configured severity.
    /// </summary>
    /// <param name="report">The report as the analyzers produced it.</param>
    public AnalysisReport Apply(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (IsEmpty)
        {
            return report;
        }

        var results = report
            .Results.Select(result =>
                result with
                {
                    // An analyzer that loses every finding still reports, with none. Dropping the
                    // result would make a disabled rule indistinguishable from an analyzer that
                    // never ran.
                    Issues = result
                        .Issues.Where(issue =>
                            !_entries.TryGetValue(issue.IssueCode, out var value)
                            || value is not null
                        )
                        .Select(Regrade)
                        .ToList(),
                }
            )
            .ToList();

        return report with
        {
            Results = results,
        };
    }

    /// <summary>
    /// Renders the policy for a human, e.g. <c>OutdatedPackage=none, LicenseRisk=Critical</c>.
    /// </summary>
    public string Describe()
    {
        return string.Join(
            ", ",
            _entries
                .OrderBy(entry => entry.Key)
                .Select(entry =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{entry.Key}={entry.Value?.ToString() ?? DisableKeyword}"
                    )
                )
        );
    }

    private AnalysisIssue Regrade(AnalysisIssue issue)
    {
        return SeverityOverrides.TryGetValue(issue.IssueCode, out var severity)
            ? issue with
            {
                Severity = severity,
            }
            : issue;
    }
}
