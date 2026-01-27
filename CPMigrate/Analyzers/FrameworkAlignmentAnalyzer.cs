using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes Target Framework divergence across projects.
/// Divergent frameworks can lead to inconsistent package resolution in CPM.
/// </summary>
public class FrameworkAlignmentAnalyzer : IAnalyzer
{
    public string Name => "Framework Alignment";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();
        var frameworks = new Dictionary<string, List<string>>();

        // We need to get frameworks for each project.
        // PackageReference doesn't have it, but we can extract it.
        var projectPaths = packageInfo.References.Select(r => r.ProjectPath).Distinct();

        foreach (var path in projectPaths)
        {
            var tfm = ProjectAnalyzer.GetTargetFramework(path);
            if (!frameworks.ContainsKey(tfm))
            {
                frameworks[tfm] = new List<string>();
            }

            frameworks[tfm].Add(Path.GetFileName(path));
        }

        if (frameworks.Count > 1)
        {
            var tfmList = string.Join(", ", frameworks.Keys.OrderBy(k => k));
            issues.Add(new AnalysisIssue(
                "Multiple Frameworks",
                $"Repository uses {frameworks.Count} different Target Frameworks: {tfmList}. Ensure package versions in Directory.Packages.props are compatible with all.",
                frameworks.Values.SelectMany(v => v).ToList()
            ));
        }

        return new AnalyzerResult(Name, issues);
    }
}
