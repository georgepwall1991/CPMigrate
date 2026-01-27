using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Interface for orchestrating package analysis.
/// </summary>
public interface IAnalysisService
{
    /// <summary>
    /// Analyzes package information and returns a report.
    /// </summary>
    AnalysisReport Analyze(ProjectPackageInfo packageInfo);
}
