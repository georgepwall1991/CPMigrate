using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates package analysis by running all registered analyzers.
/// </summary>
public class AnalysisService : IAnalysisService
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers;

    public AnalysisService()
        : this(AnalyzerCatalog.CreateDefault(SilentConsoleService.Instance))
    {
    }

    public AnalysisService(IEnumerable<IAnalyzer> analyzers)
    {
        _analyzers = analyzers.ToList();
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
