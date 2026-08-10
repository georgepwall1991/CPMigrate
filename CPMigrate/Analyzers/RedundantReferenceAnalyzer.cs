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
        var issues = packageInfo
            .GetDeclaredReferences()
            .Where(reference => !reference.IsConditional)
            .Where(reference => !reference.IsMetadataOnlyUpdate)
            .Where(reference => !reference.IsTransitive)
            .GroupBy(r => r.ProjectPath)
            .SelectMany(projectGroup =>
                projectGroup
                    .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(packageGroup => BuildIssue(packageInfo, projectGroup, packageGroup)))
            .ToList();

        return new AnalyzerResult(Name, issues);
    }

    private static AnalysisIssue BuildIssue(
        ProjectPackageInfo packageInfo,
        IGrouping<string, PackageReference> projectGroup,
        IGrouping<string, PackageReference> packageGroup)
    {
        var firstRef = projectGroup.First();
        var projectName = packageInfo.ProjectId(firstRef.ProjectPath);
        var count = packageGroup.Count();
        var versions = packageGroup.Select(r => r.Version).Distinct().ToList();

        var description = versions.Count == 1
            ? $"Referenced {count} times with version {versions[0]}"
            : $"Referenced {count} times with versions: {string.Join(", ", versions)}";

        return new AnalysisIssue(
            packageGroup.Key,
            description,
            [projectName],
            AnalysisIssueCode.RedundantReference,
            AnalysisSeverity.Low,
            Fixable: true);
    }
}
