using System.Text;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Writes the persistent Markdown rollup of a <c>--batch</c> run.
///
/// The console summary disappears with the terminal scrollback; this file is what a team attaches
/// to a CI job or pull request. The layout is deliberately fixed — same columns in the same order
/// every run, one row per solution in batch order — so two reports from different runs diff as
/// cleanly as the code changes between them.
/// </summary>
public static class BatchReportWriter
{
    /// <summary>
    /// Renders the rollup for a completed batch run. Deterministic given the result: nothing here
    /// reads the clock or the environment, so the same <see cref="BatchResult"/> always renders
    /// byte-for-byte identically.
    /// </summary>
    internal static string Render(BatchResult result)
    {
        var totals = result.Totals;
        var builder = new StringBuilder();

        builder.AppendLine("# CPMigrate Batch Report");
        builder.AppendLine();
        builder.AppendLine($"- Operation: {result.Operation}");
        builder.AppendLine($"- Date: {result.Timestamp}");
        builder.AppendLine($"- Tool version: {OutputMetadata.CurrentVersion}");
        builder.AppendLine($"- Dry run: {(result.DryRun ? "yes" : "no")}");
        builder.AppendLine();
        builder.AppendLine("## Solutions");
        builder.AppendLine();
        builder.AppendLine("| Solution | Exit Code | Projects Processed | Packages Found | Props File |");
        builder.AppendLine("| --- | ---: | ---: | ---: | --- |");

        foreach (var solution in result.Solutions)
        {
            var summary = solution.Summary;
            builder.AppendLine(
                $"| {Cell(solution.Name)} | {solution.ExitCode} "
                    + $"| {(summary?.ProjectsProcessed ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"| {(summary?.PackagesFound ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"| {Cell(solution.PropsFile ?? string.Empty)} |"
            );
        }

        builder.AppendLine(
            $"| **Total** |  | {totals.ProjectsProcessed.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + $"| {totals.PackagesFound.ToString(System.Globalization.CultureInfo.InvariantCulture)} |  |"
        );
        builder.AppendLine();

        var failures = result.Solutions.Where(s => !s.Success).ToList();
        if (failures.Count > 0)
        {
            builder.AppendLine("## Failures");
            builder.AppendLine();
            foreach (var failure in failures)
            {
                builder.AppendLine($"- {failure.Name} (exit code {failure.ExitCode})");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes the rollup for a completed batch run to <paramref name="path"/>, creating parent
    /// directories when they do not exist yet.
    /// </summary>
    public static void Write(BatchResult result, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Render(result));
    }

    /// <summary>Escapes a value for a Markdown table cell, so a pipe in a name cannot break a column.</summary>
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
