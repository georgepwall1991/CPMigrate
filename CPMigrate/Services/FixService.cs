using CPMigrate.Fixers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Service that orchestrates automatic fixes for detected analysis issues.
/// </summary>
public class FixService
{
    private readonly List<IFixer> _fixers;
    private readonly IConsoleService _console;

    public FixService(IConsoleService console)
    {
        _console = console;
        _fixers = new List<IFixer>
        {
            new VersionInconsistencyFixer(),
            new DuplicatePackageFixer(),
            new RedundantReferenceFixer()
        };
    }

    /// <summary>
    /// Applies fixes for all issues in the analysis report.
    /// </summary>
    /// <param name="report">The analysis report containing issues to fix.</param>
    /// <param name="packageInfo">Package information from the analysis.</param>
    /// <param name="options">Options controlling fix behavior.</param>
    /// <param name="dryRun">If true, shows what would be changed without modifying files.</param>
    /// <returns>Report of all fixes applied.</returns>
    public FixReport ApplyFixes(AnalysisReport report, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        var fixReport = new FixReport();

        if (!report.HasIssues)
        {
            _console.Success("No issues to fix.");
            return fixReport;
        }

        // Collect all issues from all analyzer results
        var allIssues = report.Results
            .SelectMany(r => r.Issues)
            .ToList();

        if (allIssues.Count == 0)
        {
            _console.Success("No issues to fix.");
            return fixReport;
        }

        _console.Info($"Found {allIssues.Count} issue(s) to fix{(dryRun ? " (dry run)" : "")}...");

        foreach (var issue in allIssues)
        {
            var fixer = _fixers.FirstOrDefault(f => f.CanFix(issue));
            if (fixer == null)
            {
                _console.Warning($"No fixer available for: {issue.PackageName}");
                continue;
            }

            try
            {
                var result = fixer.Fix(issue, packageInfo, options, dryRun);
                fixReport.Results.Add(result);

                if (result.Success && result.Changes.Count > 0)
                {
                    WriteFixResult(issue, result, dryRun);
                }
                else if (!result.Success)
                {
                    _console.Error($"Failed to fix {issue.PackageName}: {result.Description}");
                }
            }
            catch (Exception ex)
            {
                var failedResult = FixResult.Failed($"Exception: {ex.Message}");
                fixReport.Results.Add(failedResult);
                _console.Error($"Error fixing {issue.PackageName}: {ex.Message}");
            }
        }

        WriteSummary(fixReport, dryRun);
        return fixReport;
    }

    private void WriteFixResult(AnalysisIssue issue, FixResult result, bool dryRun)
    {
        var prefix = dryRun ? "[DRY RUN] Would fix" : "Fixed";
        _console.Success($"{prefix}: {result.Description}");

        foreach (var change in result.Changes)
        {
            _console.Info($"  {change.ChangeType}: {Path.GetFileName(change.FilePath)}");
            if (!string.IsNullOrEmpty(change.Before) && !string.IsNullOrEmpty(change.After))
            {
                _console.Info($"    - {change.Before}");
                _console.Info($"    + {change.After}");
            }
        }
    }

    private void WriteSummary(FixReport report, bool dryRun)
    {
        _console.WriteLine();

        if (report.HasChanges)
        {
            var action = dryRun ? "Would apply" : "Applied";
            _console.Success($"{action} {report.TotalFixesApplied} fix(es) affecting {report.TotalFileChanges} file(s).");

            if (dryRun)
            {
                _console.Info("Run with --fix (without --fix-dry-run) to apply these changes.");
            }
        }
        else
        {
            _console.Info("No changes were needed.");
        }

        if (report.FailedFixes.Count > 0)
        {
            _console.Warning($"{report.FailedFixes.Count} issue(s) could not be fixed automatically.");
        }
    }
}
