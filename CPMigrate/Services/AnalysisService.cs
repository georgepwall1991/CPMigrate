using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates package analysis by running all registered analyzers.
/// </summary>
public class AnalysisService
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers;

    public AnalysisService(IEnumerable<IAnalyzer>? analyzers = null)
    {
        _analyzers = analyzers?.ToList() ?? new List<IAnalyzer>
        {
            new VersionInconsistencyAnalyzer(),
            new DuplicatePackageAnalyzer(),
            new RedundantReferenceAnalyzer(),
            new TransitiveDependencyAnalyzer()
        };
    }

    /// <summary>
    /// Runs all analyzers on the provided package information.
    /// </summary>
    /// <param name="packageInfo">Package references collected from projects.</param>
    /// <returns>Combined analysis report from all analyzers.</returns>
    public AnalysisReport Analyze(ProjectPackageInfo packageInfo)
    {
        var results = _analyzers
            .Select(analyzer => analyzer.Analyze(packageInfo))
            .ToList();

        return new AnalysisReport(
            packageInfo.ProjectCount,
            packageInfo.TotalReferences,
            results
        );
    }
}
