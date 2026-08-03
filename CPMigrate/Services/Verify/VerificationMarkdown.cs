using System.Text;
using CPMigrate.Models;

namespace CPMigrate.Services.Verify;

/// <summary>
/// Renders the verification receipt as Markdown, for a CI job summary or a pull request comment.
/// </summary>
/// <remarks>
/// This is the artefact the feature exists to produce. A migration PR is sixty changed files and a
/// reviewer with no way to answer "does this change what we ship?"; pasting the receipt into the
/// description answers it before anyone opens a diff. It also fills the gap where the payload's
/// <c>conflictsResolved</c> integer used to be the only record that any decision was ever made.
/// </remarks>
public static class VerificationMarkdown
{
    /// <summary>
    /// Rows are capped so a monorepo cannot produce a comment GitHub refuses to render. What was
    /// dropped is stated rather than silently truncated — a table that stops without saying so reads
    /// as a complete one.
    /// </summary>
    private const int RowLimit = 50;

    public static string Format(VerificationReport report, bool strict)
    {
        var markdown = new StringBuilder();

        WriteHeading(markdown, report, strict);

        if (report.Verdict == VerificationVerdict.Failed)
        {
            WriteFailure(markdown, report);
            WriteFooter(markdown);
            return markdown.ToString();
        }

        WriteScale(markdown, report);
        WriteChanges(markdown, report);
        WriteDecisions(markdown, report);
        WriteFooter(markdown);

        return markdown.ToString();
    }

    private static void WriteHeading(StringBuilder markdown, VerificationReport report, bool strict)
    {
        var (icon, headline) = report.Verdict switch
        {
            VerificationVerdict.Unchanged => (
                "✅",
                "every resolved version is exactly what it was — this migration changes no shipped code"
            ),
            VerificationVerdict.ExplainedDrift when strict => (
                "❌",
                $"{report.ChangedCount} resolved version(s) moved. All accounted for, but `--verify-strict` "
                    + "asked for a migration that changes nothing at all"
            ),
            VerificationVerdict.ExplainedDrift => (
                "⚠️",
                $"{report.ChangedCount} resolved version(s) moved, all accounted for by "
                    + $"{report.Decisions.Count} deliberate decision(s)"
            ),
            VerificationVerdict.UnexplainedDrift => (
                "❌",
                $"{report.UnexplainedCount} of {report.ChangedCount} changed version(s) are not accounted "
                    + "for by anything this migration decided"
            ),
            _ => ("❌", "no verdict could be reached"),
        };

        markdown.AppendLine($"## {icon} CPMigrate — resolved-graph verification");
        markdown.AppendLine();
        markdown.AppendLine(headline.EndsWith('.') ? headline : headline + ".");
        markdown.AppendLine();
    }

    private static void WriteFailure(StringBuilder markdown, VerificationReport report)
    {
        markdown.AppendLine($"**{Escape(report.FailureReason ?? "no reason recorded")}**");
        markdown.AppendLine();

        if (report.IntegrityFailures.Count > 0)
        {
            markdown.AppendLine("| Project | Target framework | Reason |");
            markdown.AppendLine("| --- | --- | --- |");

            foreach (var failure in report.IntegrityFailures.Take(RowLimit))
            {
                markdown.AppendLine(
                    $"| `{Escape(failure.ProjectPath)}` | {Escape(failure.TargetFramework ?? "—")} | {Escape(failure.Reason)} |"
                );
            }

            WriteTruncationNote(markdown, report.IntegrityFailures.Count);
            markdown.AppendLine();
        }

        markdown.AppendLine(
            "> Not knowing whether the graph moved is not the same as knowing it did not."
        );
        markdown.AppendLine();
    }

    private static void WriteScale(StringBuilder markdown, VerificationReport report)
    {
        // Before the findings, deliberately. "0 changed" over four projects out of forty is a
        // different statement from "0 changed" over all of them.
        markdown.AppendLine("| | |");
        markdown.AppendLine("| --- | ---: |");
        markdown.AppendLine(
            $"| Projects restored | {report.ProjectsRestored} / {report.ProjectsExpected} |"
        );
        markdown.AppendLine($"| Resolved versions | {report.ResolvedVersionCount} |");
        markdown.AppendLine($"| Unchanged | {report.UnchangedCount} |");
        markdown.AppendLine($"| Changed | {report.ChangedCount} |");
        markdown.AppendLine();
    }

    private static void WriteChanges(StringBuilder markdown, VerificationReport report)
    {
        if (report.Changes.Count == 0)
        {
            return;
        }

        markdown.AppendLine("### What moved");
        markdown.AppendLine();
        markdown.AppendLine("| | Project | Framework | Package | Before | After | Why |");
        markdown.AppendLine("| :-: | --- | --- | --- | --- | --- | --- |");

        foreach (var change in report.Changes.Take(RowLimit))
        {
            var icon = change.Kind == DriftExplanation.Unexplained ? "❌" : "↳";
            var why =
                change.Kind == DriftExplanation.Unexplained
                    ? "**unexplained**"
                    : Escape(change.Description);

            markdown.AppendLine(
                $"| {icon} "
                    + $"| `{Escape(change.Change.ProjectPath)}` "
                    + $"| {Escape(change.Change.TargetFramework)} "
                    + $"| `{Escape(change.Change.PackageId)}` "
                    + $"| {Escape(change.Change.Before ?? "—")} "
                    + $"| {Escape(change.Change.After ?? "—")} "
                    + $"| {why} |"
            );
        }

        WriteTruncationNote(markdown, report.Changes.Count);
        markdown.AppendLine();
    }

    private static void WriteDecisions(StringBuilder markdown, VerificationReport report)
    {
        if (report.Decisions.Count == 0)
        {
            return;
        }

        markdown.AppendLine("### Decisions this migration made for you");
        markdown.AppendLine();
        markdown.AppendLine("| Package | Won | Over | Chosen by |");
        markdown.AppendLine("| --- | --- | --- | --- |");

        foreach (var decision in report.Decisions.Take(RowLimit))
        {
            var others = decision
                .Candidates.Where(candidate =>
                    !string.Equals(
                        candidate.Version,
                        decision.ResolvedVersion,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(candidate => $"{candidate.Version} ({candidate.Projects.Count})")
                .ToList();

            markdown.AppendLine(
                $"| `{Escape(decision.PackageId)}` "
                    + $"| {Escape(decision.ResolvedVersion)} "
                    + $"| {Escape(others.Count == 0 ? "—" : string.Join(", ", others))} "
                    + $"| {Escape(DescribeSource(decision.Source))} |"
            );
        }

        WriteTruncationNote(markdown, report.Decisions.Count);
        markdown.AppendLine();
    }

    /// <summary>
    /// The flag that actually produced a decision, so the receipt is replayable.
    /// </summary>
    /// <remarks>
    /// <c>Interactive</c> is not a <c>--conflict-strategy</c> value; the control is
    /// <c>--interactive-conflicts</c>. Printing `--conflict-strategy Interactive` told a reader to run
    /// a command that does not parse — a receipt that cannot be acted on. Cross-review caught it.
    /// </remarks>
    private static string DescribeSource(ConflictDecisionSource source) =>
        source == ConflictDecisionSource.Interactive
            ? "`--interactive-conflicts`"
            : $"`--conflict-strategy {source}`";

    private static void WriteTruncationNote(StringBuilder markdown, int total)
    {
        if (total <= RowLimit)
        {
            return;
        }

        markdown.AppendLine();
        markdown.AppendLine(
            $"_Showing {RowLimit} of {total}. The full set is in `--output Json`._"
        );
    }

    private static void WriteFooter(StringBuilder markdown)
    {
        markdown.AppendLine("---");
        markdown.AppendLine();
        markdown.AppendLine(
            $"<sub>CPMigrate {OutputMetadata.CurrentVersion} · `--verify` · output schema {OutputMetadata.SchemaVersion}</sub>"
        );
    }

    /// <summary>
    /// Neutralises the pipe, which would otherwise split a cell and silently shift every column to
    /// its right — a package ID or a restore error is not guaranteed not to contain one.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);
}
