using CPMigrate.Fixers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Interface for orchestrating automatic fixes for detected analysis issues.
/// </summary>
public interface IFixService
{
    /// <summary>
    /// Applies fixes for all issues in the analysis report.
    /// </summary>
    FixReport ApplyFixes(AnalysisReport report, ProjectPackageInfo packageInfo, Options options, bool dryRun);
}
