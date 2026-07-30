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
        var packageGroups = packageInfo
            .References
            // A version pinned behind a Condition is deliberate — a multi-targeted project saying the
            // newer package does not support the older framework. Comparing it against the others reads
            // that as an inconsistency, and since the finding is fixable it would be "corrected" by
            // unifying to the highest version, breaking the target that needed the older one. Reporting a
            // finding the fixer then refuses to act on is its own kind of wrong, so it is not reported.
            .Where(reference =>
                !packageInfo.IsConditionallyDeclared(reference.ProjectPath, reference.PackageName, reference.Version)
            )
            .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.Version).Distinct().Count() > 1);

        foreach (var group in packageGroups)
        {
            // Build description showing which versions are where
            var versionsByProject = group
                .GroupBy(r => r.Version)
                .Select(vg => $"{vg.Key} ({string.Join(", ", vg.Select(r => packageInfo.ProjectId(r.ProjectPath)))})")
                .ToList();

            var description = string.Join(", ", versionsByProject);
            var affectedProjects = group.Select(r => packageInfo.ProjectId(r.ProjectPath)).Distinct().ToList();

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
