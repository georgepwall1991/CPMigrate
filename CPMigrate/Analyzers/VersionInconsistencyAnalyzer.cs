using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes packages for version inconsistencies across projects.
/// Detects when the same package has different versions in different projects.
/// </summary>
public class VersionInconsistencyAnalyzer : IAnalyzer
{
    public string Name => "Version Inconsistencies";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();

        // Group by package name (case-insensitive) to find all versions
        var packageGroups = packageInfo.References
            .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.Version).Distinct().Count() > 1);

        foreach (var group in packageGroups)
        {
            // Build description showing which versions are where
            var versionsByProject = group
                .GroupBy(r => r.Version)
                .Select(vg => $"{vg.Key} ({string.Join(", ", vg.Select(r => r.ProjectName))})")
                .ToList();

            var description = string.Join(", ", versionsByProject);
            var affectedProjects = group.Select(r => r.ProjectName).Distinct().ToList();

            issues.Add(new AnalysisIssue(
                group.Key,
                description,
                affectedProjects,
                AnalysisIssueCode.VersionInconsistency,
                AnalysisSeverity.Moderate,
                Fixable: true
            ));
        }

        return new AnalyzerResult(Name, issues);
    }
}
