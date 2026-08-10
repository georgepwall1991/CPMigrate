using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes packages for duplicate references with different casing.
/// Detects when the same package appears with inconsistent casing (e.g., "Newtonsoft.Json" vs "newtonsoft.json").
/// </summary>
public class DuplicatePackageAnalyzer : IAnalyzer
{
    public string Name => "Duplicate Packages (Casing)";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();

        // Group by lowercase name to find different casing variations
        var casingGroups = packageInfo.GetDeclaredReferences()
            .Where(r => !r.IsTransitive)
            .GroupBy(r => r.PackageName.ToLowerInvariant())
            .Where(g => g.Select(r => r.PackageName).Distinct(StringComparer.Ordinal).Count() > 1);

        foreach (var group in casingGroups)
        {
            var variations = group
                .Select(r => r.PackageName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Defensive check - variations should never be empty given the Where clause
            if (variations.Count == 0)
            {
                continue;
            }

            var description = $"Found {variations.Count} casing variations: {string.Join(", ", variations)}";
            var affectedProjects = group.Select(r => packageInfo.ProjectId(r.ProjectPath)).Distinct().ToList();

            issues.Add(new AnalysisIssue(
                variations[0], // Use the first variation as the canonical name
                description,
                affectedProjects,
                AnalysisIssueCode.DuplicatePackageCasing,
                AnalysisSeverity.Low,
                Fixable: true
            ));
        }

        return new AnalyzerResult(Name, issues);
    }
}
