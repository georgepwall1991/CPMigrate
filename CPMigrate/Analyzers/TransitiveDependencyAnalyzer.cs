using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes transitive dependencies for version inconsistencies across projects.
/// Suggests pinning them in CPM if they diverge.
/// </summary>
public class TransitiveDependencyAnalyzer : IAnalyzer
{
    public string Name => "Transitive Conflicts";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();
        
        // Group by package name
        var transitiveReferences = packageInfo.References
            .Where(r => r.IsTransitive)
            .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in transitiveReferences)
        {
            var versions = group.Select(r => r.Version).Distinct().ToList();

            if (versions.Count > 1)
            {
                var packageName = group.Key;
                var versionList = string.Join(", ", versions.OrderBy(v => v));
                var projectCount = group.Select(r => r.ProjectPath).Distinct().Count();

                issues.Add(new AnalysisIssue(
                    packageName,
                    $"Transitive dependency has {versions.Count} different versions across {projectCount} projects: {versionList}. Pinning this package in Directory.Packages.props is recommended.",
                    group.Select(r => r.ProjectName).Distinct().ToList()
                ));
            }
        }

        return new AnalyzerResult(Name, issues);
    }
}
