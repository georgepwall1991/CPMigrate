using System.Globalization;
using System.Text;
using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The context a Markdown report needs beyond the findings themselves: what the gate decided, and
/// what a baseline accepted. Without it a reader sees a list of problems and cannot tell whether the
/// build passed, or why.
/// </summary>
/// <param name="FailOn">The configured failure threshold.</param>
/// <param name="GatedIssueCount">Findings that reached the threshold, when known.</param>
/// <param name="ExitCode">Exit code the run produced.</param>
/// <param name="ScanFailures">Projects that could not be scanned.</param>
/// <param name="DeepScanFailures">Opt-in package queries that failed.</param>
/// <param name="BaselinePath">Baseline read or written, when one was.</param>
/// <param name="BaselineWritten">
/// True when the run recorded a baseline. That is the run's primary outcome, and the terminal
/// confirmation is suppressed in machine-readable mode, so the report has to say it.
/// </param>
/// <param name="ProjectsScanned">
/// Projects the scan covered. Passed in rather than derived from the package references, because a
/// project with no PackageReference contributes none — so a reference-derived count under-reports,
/// and reads as zero for a solution whose projects have no packages at all.
/// </param>
/// <param name="BaselineStaleEntries">
/// Baseline entries that matched no finding this run. They suppress nothing, so leaving them out
/// would let a baseline look like it is still doing work while it rots.
/// </param>
/// <param name="BaselineUnknownRuleCodes">
/// Rule IDs the baseline cites that the catalog does not know — usually renamed or deleted rules.
/// Named so the reader can tell a dead entry from debt that was genuinely fixed.
/// </param>
public record MarkdownReportContext(
    FailOnSeverity FailOn = FailOnSeverity.Info,
    int? GatedIssueCount = null,
    int ExitCode = 0,
    int ScanFailures = 0,
    int DeepScanFailures = 0,
    string? BaselinePath = null,
    bool BaselineWritten = false,
    int? ProjectsScanned = null,
    int BaselineStaleEntries = 0,
    IReadOnlyList<string>? BaselineUnknownRuleCodes = null
);

/// <summary>
/// Renders an <see cref="AnalysisReport"/> as GitHub-flavoured Markdown, for a job summary or a pull
/// request comment.
///
/// This exists because neither existing format reaches a human at the moment they need it: JSON is
/// for parsers, and SARIF only surfaces findings that map to a line in the diff being reviewed. A
/// dependency problem is usually about the solution as a whole, so it never appears on the diff at
/// all — and a reviewer will not go digging in build logs for it.
/// </summary>
public static class MarkdownFormatter
{
    /// <summary>Findings listed in full before the report collapses them behind a disclosure.</summary>
    private const int InlineFindingLimit = 25;

    /// <summary>
    /// Formats an analysis report as Markdown.
    /// </summary>
    /// <param name="report">The findings to render.</param>
    /// <param name="packageInfo">Scan totals shown in the header.</param>
    /// <param name="context">Gate and baseline context, so the verdict is explainable.</param>
    /// <returns>GitHub-flavoured Markdown.</returns>
    public static string Format(
        AnalysisReport report,
        ProjectPackageInfo packageInfo,
        MarkdownReportContext? context = null
    )
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(packageInfo);

        context ??= new MarkdownReportContext();
        var markdown = new StringBuilder();

        WriteHeading(markdown, report, context);
        WriteBaselineWrittenNote(markdown, report, context);
        WriteScanSummary(markdown, report, packageInfo, context);
        WriteIncompleteScanWarning(markdown, context);
        WriteSeverityBreakdown(markdown, report);
        WriteFindings(markdown, report);
        WriteBaselineNote(markdown, report, context);
        WriteFooter(markdown);

        return markdown.ToString();
    }

    private static void WriteHeading(
        StringBuilder markdown,
        AnalysisReport report,
        MarkdownReportContext context
    )
    {
        var gated = context.GatedIssueCount ?? report.CountAtOrAbove(GateSeverity(context.FailOn));
        var incomplete =
            context.ScanFailures > 0
            || context.DeepScanFailures > 0
            // A non-zero exit with no findings to explain it means the analysis did not produce a
            // usable result — no projects discovered, a file error. Rendering that as a clean bill
            // of health would contradict the command's own exit code.
            || (context.ExitCode != 0 && gated == 0);

        if (context.BaselineWritten)
        {
            // A baseline run is not gating on anything, so a pass/fail verdict would be misleading.
            markdown.AppendLine("## ✅ CPMigrate — baseline recorded");
            markdown.AppendLine();
            return;
        }

        var (icon, verdict) = (gated > 0, incomplete) switch
        {
            (true, _) => ("❌", $"{gated} finding(s) at or above **{context.FailOn}**"),
            (false, true) => ("⚠️", DescribeIncompleteRun(context)),
            (false, false) when report.HasIssues => (
                "✅",
                "No findings reached the failure threshold"
            ),
            _ => ("✅", "No findings"),
        };

        markdown.AppendLine(
            CultureInfo.InvariantCulture,
            $"## {icon} CPMigrate — dependency analysis"
        );
        markdown.AppendLine();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"**{verdict}.**");
        markdown.AppendLine();
    }

    /// <summary>
    /// Explains why a run cannot be trusted, distinguishing a partial scan from one that produced no
    /// analysis at all.
    /// </summary>
    private static string DescribeIncompleteRun(MarkdownReportContext context)
    {
        if (context.ScanFailures > 0 || context.DeepScanFailures > 0)
        {
            return "Scan incomplete — results cannot be trusted";
        }

        return $"Analysis did not complete (exit {context.ExitCode}) — no results to report";
    }

    /// <summary>
    /// States the outcome of a <c>--write-baseline</c> run. It is the whole point of the command, and
    /// the terminal confirmation is suppressed when a machine-readable format is requested.
    /// </summary>
    private static void WriteBaselineWrittenNote(
        StringBuilder markdown,
        AnalysisReport report,
        MarkdownReportContext context
    )
    {
        if (!context.BaselineWritten)
        {
            return;
        }

        markdown.AppendLine(
            $"Recorded **{report.TotalIssues} finding(s)** as the accepted baseline in "
                + $"`{Escape(context.BaselinePath ?? Options.BaselineDefaultFileName)}`. "
                + "Commit it: subsequent runs report these findings without failing the build."
        );
        markdown.AppendLine();
    }

    private static void WriteScanSummary(
        StringBuilder markdown,
        AnalysisReport report,
        ProjectPackageInfo packageInfo,
        MarkdownReportContext context
    )
    {
        markdown.AppendLine("| | |");
        markdown.AppendLine("|---|---|");
        markdown.AppendLine(
            CultureInfo.InvariantCulture,
            $"| Projects scanned | {context.ProjectsScanned ?? packageInfo.ProjectCount} |"
        );
        markdown.AppendLine(
            CultureInfo.InvariantCulture,
            $"| Package references | {packageInfo.TotalReferences} |"
        );
        markdown.AppendLine(CultureInfo.InvariantCulture, $"| Findings | {report.TotalIssues} |");

        if (report.SuppressedCount > 0)
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"| Accepted in baseline | {report.SuppressedCount} |"
            );
        }

        if (context.BaselineStaleEntries > 0)
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"| Stale baseline entries | {context.BaselineStaleEntries} |"
            );
        }

        markdown.AppendLine();
    }

    private static void WriteIncompleteScanWarning(
        StringBuilder markdown,
        MarkdownReportContext context
    )
    {
        if (context.ScanFailures == 0 && context.DeepScanFailures == 0)
        {
            if (context.ExitCode != 0)
            {
                markdown.AppendLine("> [!WARNING]");
                markdown.AppendLine(
                    $"> CPMigrate exited {context.ExitCode} without completing the analysis, so this "
                        + "report is not evidence that the dependencies are healthy."
                );
                markdown.AppendLine();
            }

            return;
        }

        // Stated prominently rather than as a footnote: an incomplete scan reporting no findings
        // reads exactly like a clean one, and that is the mistake this warning exists to prevent.
        markdown.AppendLine("> [!WARNING]");
        markdown.AppendLine(
            "> This scan did not complete, so the findings below are incomplete — "
                + "absence of a finding does not mean absence of a problem."
        );

        if (context.ScanFailures > 0)
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"> {context.ScanFailures} project(s) failed to scan."
            );
        }

        if (context.DeepScanFailures > 0)
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"> {context.DeepScanFailures} package quer(ies) failed (`--audit`/`--outdated`/`--deprecated`/`--licenses`)."
            );
        }

        markdown.AppendLine();
    }

    private static void WriteSeverityBreakdown(StringBuilder markdown, AnalysisReport report)
    {
        if (!report.HasIssues)
        {
            return;
        }

        var counts = Enum.GetValues<AnalysisSeverity>()
            .Reverse()
            .Select(severity => (Severity: severity, Count: CountExactly(report, severity)))
            .Where(entry => entry.Count > 0)
            .ToList();

        if (counts.Count == 0)
        {
            return;
        }

        markdown.AppendLine("| Severity | Findings |");
        markdown.AppendLine("|---|---:|");
        foreach (var (severity, count) in counts)
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {SeverityIcon(severity)} {severity} | {count} |"
            );
        }

        markdown.AppendLine();
    }

    private static void WriteFindings(StringBuilder markdown, AnalysisReport report)
    {
        var findings = report
            .Results.SelectMany(result => result.Issues)
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (findings.Count == 0)
        {
            return;
        }

        var collapse = findings.Count > InlineFindingLimit;
        if (collapse)
        {
            // A job summary with hundreds of rows buries everything else on the page.
            markdown.AppendLine("<details>");
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"<summary>All {findings.Count} findings</summary>"
            );
            markdown.AppendLine();
        }

        markdown.AppendLine("| Severity | Rule | Package | Projects | Details |");
        markdown.AppendLine("|---|---|---|---|---|");

        foreach (var issue in findings)
        {
            var rule = AnalysisRuleCatalog.Get(issue.IssueCode);
            var suppressed = issue.Suppressed ? " *(baselined)*" : string.Empty;

            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {SeverityIcon(issue.Severity)} {issue.Severity} "
                    + $"| [{rule.Id}]({rule.HelpUri}) "
                    + $"| `{Escape(issue.PackageName)}`{suppressed} "
                    + $"| {FormatProjects(issue.AffectedProjects)} "
                    + $"| {Escape(issue.Description)} |"
            );
        }

        markdown.AppendLine();

        if (collapse)
        {
            markdown.AppendLine("</details>");
            markdown.AppendLine();
        }
    }

    private static void WriteBaselineNote(
        StringBuilder markdown,
        AnalysisReport report,
        MarkdownReportContext context
    )
    {
        if (string.IsNullOrWhiteSpace(context.BaselinePath))
        {
            return;
        }

        var noted = false;
        if (report.SuppressedCount > 0)
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"{report.SuppressedCount} finding(s) marked *(baselined)* are accepted in "
                    + $"`{Escape(context.BaselinePath)}` and do not fail the build."
            );
            noted = true;
        }

        if (context.BaselineStaleEntries > 0)
        {
            // A stale entry suppresses nothing: the debt it accepted is gone. Left unmentioned it
            // makes the baseline look like it is still doing work while it quietly rots.
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"{context.BaselineStaleEntries} baseline entr(ies) matched no finding — they are "
                    + $"dead weight. Remove the dead entries from the baseline file by hand; --write-baseline would also accept this run's new findings."
            );
            noted = true;
        }

        if (context.BaselineUnknownRuleCodes is { Count: > 0 })
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"Baseline cites {context.BaselineUnknownRuleCodes.Count} rule ID(s) "
                    + $"CPMigrate does not know ({Escape(string.Join(", ", context.BaselineUnknownRuleCodes))})"
                    + $" — likely renamed or removed rules rather than fixed findings. Run "
                    + $"`cpmigrate --explain all` for the current rule IDs."
            );
            noted = true;
        }

        if (noted)
        {
            markdown.AppendLine();
        }
    }

    private static void WriteFooter(StringBuilder markdown)
    {
        markdown.AppendLine(
            CultureInfo.InvariantCulture,
            $"<sub>CPMigrate {OutputMetadata.CurrentVersion} — "
                + $"[rule reference]({AnalysisRuleCatalog.DocumentationBaseUri})</sub>"
        );
    }

    /// <summary>
    /// Renders up to three project paths inline and summarises the rest, so one finding spanning
    /// forty projects does not produce an unreadable row.
    /// </summary>
    private static string FormatProjects(IReadOnlyList<string> projects)
    {
        if (projects.Count == 0)
        {
            return "—";
        }

        var shown = projects.Take(3).Select(p => $"`{Escape(p)}`");
        var remainder = projects.Count - 3;

        return remainder > 0
            ? $"{string.Join(", ", shown)} +{remainder} more"
            : string.Join(", ", shown);
    }

    private static int CountExactly(AnalysisReport report, AnalysisSeverity severity)
    {
        return report.Results.Sum(result =>
            result.Issues.Count(issue => issue.Severity == severity)
        );
    }

    /// <summary>
    /// Maps the threshold onto a severity for counting. <see cref="FailOnSeverity.Never"/> has no
    /// severity above it, so it is represented by the highest one and handled by the caller.
    /// </summary>
    private static AnalysisSeverity GateSeverity(FailOnSeverity failOn)
    {
        return failOn == FailOnSeverity.Never
            ? AnalysisSeverity.Critical
            : (AnalysisSeverity)failOn;
    }

    private static string SeverityIcon(AnalysisSeverity severity)
    {
        return severity switch
        {
            AnalysisSeverity.Critical => "🛑",
            AnalysisSeverity.High => "🔴",
            AnalysisSeverity.Moderate => "🟠",
            AnalysisSeverity.Low => "🟡",
            _ => "🔵",
        };
    }

    /// <summary>
    /// Escapes the characters that would break a Markdown table cell. Package names and project
    /// paths are attacker-adjacent input in the sense that they come from files CPMigrate did not
    /// write, and a stray pipe silently corrupts the whole table.
    /// </summary>
    private static string Escape(string value)
    {
        return value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);
    }
}
