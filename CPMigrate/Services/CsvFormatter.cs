using System.Text;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Renders analysis findings as CSV for spreadsheet analysis. One row per finding,
/// with columns for rule, severity, package, description, and affected projects.
/// </summary>
internal static class CsvFormatter
{
    public static string Format(AnalysisReport report, ProjectPackageInfo packageInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Rule,Severity,Package,Description,AffectedProjects,Fixable");

        foreach (var result in report.Results)
        {
            foreach (var issue in result.Issues)
            {
                sb.AppendLine(string.Join(",",
                    Escape(result.AnalyzerName),
                    Escape(issue.Severity.ToString()),
                    Escape(issue.PackageName),
                    Escape(issue.Description),
                    Escape(string.Join("; ", issue.AffectedProjects)),
                    issue.Fixable ? "true" : "false"));
            }
        }

        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
