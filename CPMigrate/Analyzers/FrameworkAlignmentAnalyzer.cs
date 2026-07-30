using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes Target Framework divergence across projects.
/// Divergent frameworks can lead to inconsistent package resolution in CPM.
/// </summary>
public class FrameworkAlignmentAnalyzer : IAnalyzer
{
    private readonly IProjectFileScanner _projectFileScanner;

    public FrameworkAlignmentAnalyzer(IProjectFileScanner? projectFileScanner = null)
    {
        _projectFileScanner = projectFileScanner ?? new ProjectFileScanner(SilentConsoleService.Instance);
    }

    public string Name => "Framework Alignment";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var frameworks = packageInfo.References
            .Select(r => r.ProjectPath)
            .Distinct()
            .GroupBy(path => _projectFileScanner.GetTargetFramework(path))
            .ToDictionary(
                g => g.Key,
                g => g.Select(path => packageInfo.ProjectId(path)).ToList());

        if (frameworks.Count <= 1)
        {
            return new AnalyzerResult(Name, []);
        }

        var tfmList = string.Join(", ", frameworks.Keys.OrderBy(k => k));
        var issue = new AnalysisIssue(
            "Multiple Frameworks",
            $"Repository uses {frameworks.Count} different Target Frameworks: {tfmList}. Ensure package versions in Directory.Packages.props are compatible with all.",
            frameworks.Values.SelectMany(v => v).ToList(),
            AnalysisIssueCode.FrameworkAlignment,
            AnalysisSeverity.Info,
            Fixable: false);

        return new AnalyzerResult(Name, [issue]);
    }
}
