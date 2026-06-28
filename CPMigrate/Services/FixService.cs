using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Services;

/// <summary>
/// Service that orchestrates automatic fixes for detected analysis issues.
/// </summary>
public class FixService : IFixService
{
    private readonly List<IFixer> _fixers;
    private readonly IConsoleService _console;

    public FixService(IConsoleService console, IEnumerable<IFixer> fixers)
    {
        _console = console;
        _fixers = fixers.ToList();
    }

    public FixService(IConsoleService console, VersionResolver? versionResolver = null)
        : this(console, FixerCatalog.CreateDefault(versionResolver ?? new VersionResolver(console)))
    {
    }

    /// <summary>
    /// Applies fixes for all issues in the analysis report.
    /// </summary>
    /// <param name="report">The analysis report containing issues to fix.</param>
    /// <param name="packageInfo">Package information from the analysis.</param>
    public FixReport ApplyFixes(AnalysisReport report, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return ApplyFixes(
            report,
            packageInfo,
            new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    /// <param name="request">Mode-specific fix settings.</param>
    /// <returns>Report of all fixes applied.</returns>
    public FixReport ApplyFixes(AnalysisReport report, ProjectPackageInfo packageInfo, FixRequest request)
    {
        var fixReport = new FixReport();

        var allIssues = CollectIssues(report);
        if (allIssues.Count == 0)
        {
            _console.Success("No issues to fix.");
            return fixReport;
        }

        _console.Info($"Found {allIssues.Count} issue(s) to fix{(request.DryRun ? " (dry run)" : "")}...");

        foreach (var issue in allIssues)
        {
            fixReport.Results.Add(ApplyFixToIssue(issue, packageInfo, request));
        }

        WriteSummary(fixReport, request.DryRun);
        return fixReport;
    }

    private static List<AnalysisIssue> CollectIssues(AnalysisReport report)
    {
        if (!report.HasIssues)
        {
            return new List<AnalysisIssue>();
        }

        return report.Results
            .SelectMany(r => r.Issues)
            .ToList();
    }

    private FixResult ApplyFixToIssue(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        var fixer = _fixers.FirstOrDefault(f => f.CanFix(issue));
        if (fixer == null)
        {
            _console.Warning($"No fixer available for: {issue.PackageName}");
            return FixResult.NoFixNeeded($"No fixer available for: {issue.PackageName}");
        }

        try
        {
            var result = fixer.Fix(issue, packageInfo, request);
            if (!result.Success)
            {
                _console.Error($"Failed to fix {issue.PackageName}: {result.Description}");
                return result;
            }

            WriteFixResult(result, request.DryRun);
            return result;
        }
        catch (Exception ex)
        {
            _console.Error($"Error fixing {issue.PackageName}: {ex.Message}");
            return FixResult.Failed($"Exception: {ex.Message}");
        }
    }

    private void WriteFixResult(FixResult result, bool dryRun)
    {
        if (result.Changes.Count == 0)
        {
            return;
        }

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

        var failedFixes = report.GetFailedFixes();
        if (failedFixes.Count > 0)
        {
            _console.Warning($"{failedFixes.Count} issue(s) could not be fixed automatically.");
        }
    }
}
