using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes packages for redundant references within the same project.
/// Detects when the same package is referenced multiple times in a single project file.
/// </summary>
public class RedundantReferenceAnalyzer : IAnalyzer
{
    public string Name => "Redundant References";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();

        // Group by project, then by package name (case-insensitive)
        var projectGroups = packageInfo.References
            .GroupBy(r => r.ProjectPath);

        foreach (var projectGroup in projectGroups)
        {
            var redundantPackages = projectGroup
                .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var packageGroup in redundantPackages)
            {
                // GroupBy guarantees non-empty groups, but FirstOrDefault is defensive
                var firstRef = projectGroup.FirstOrDefault();
                if (firstRef == null) continue;

                var projectName = firstRef.ProjectName;
                var count = packageGroup.Count();
                var versions = packageGroup.Select(r => r.Version).Distinct().ToList();

                var description = versions.Count == 1
                    ? $"Referenced {count} times with version {versions[0]}"
                    : $"Referenced {count} times with versions: {string.Join(", ", versions)}";

                issues.Add(new AnalysisIssue(
                    packageGroup.Key,
                    description,
                    new List<string> { projectName }
                ));
            }
        }

        return new AnalyzerResult(Name, issues);
    }
}
