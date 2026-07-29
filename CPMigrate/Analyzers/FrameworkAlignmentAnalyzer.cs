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
        List<AnalysisIssue> issues = [];
        Dictionary<string, List<string>> frameworks = [];

        // We need to get frameworks for each project.
        // PackageReference doesn't have it, but we can extract it.
        var projectPaths = packageInfo.References.Select(r => r.ProjectPath).Distinct();

        foreach (var path in projectPaths)
        {
            var tfm = _projectFileScanner.GetTargetFramework(path);
            if (!frameworks.TryGetValue(tfm, out var list))
            {
                list = [];
                frameworks[tfm] = list;
            }

            list.Add(packageInfo.ProjectId(path));
        }

        if (frameworks.Count > 1)
        {
            var tfmList = string.Join(", ", frameworks.Keys.OrderBy(k => k));
            issues.Add(new AnalysisIssue(
                "Multiple Frameworks",
                $"Repository uses {frameworks.Count} different Target Frameworks: {tfmList}. Ensure package versions in Directory.Packages.props are compatible with all.",
                frameworks.Values.SelectMany(v => v).ToList(),
                AnalysisIssueCode.FrameworkAlignment,
                AnalysisSeverity.Info,
                Fixable: false
            ));
        }

        return new AnalyzerResult(Name, issues);
    }
}
