using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Reports deprecated packages and suggested alternatives.
/// </summary>
public class DeprecatedPackageAnalyzer : IAnalyzer
{
    public string Name => "Deprecated Packages";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        if (packageInfo.DeprecatedPackages == null || packageInfo.DeprecatedPackages.Count == 0)
        {
            return new AnalyzerResult(Name, []);
        }

        var issues = packageInfo.DeprecatedPackages
            .GroupBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sample = group.First();
                var affectedProjects = group.Select(p => packageInfo.ProjectId(p.ProjectPath)).Distinct().ToList();
                var reasons = sample.Reasons.Count == 0 ? "Deprecated" : string.Join(", ", sample.Reasons);
                var alt = string.IsNullOrWhiteSpace(sample.AlternativePackage)
                    ? string.Empty
                    : $" Alternative: {sample.AlternativePackage} {sample.AlternativeVersionRange}".TrimEnd();

                return new AnalysisIssue(
                    sample.PackageName,
                    $"{reasons}.{alt}".Trim(),
                    affectedProjects,
                    AnalysisIssueCode.DeprecatedPackage,
                    AnalysisSeverity.Moderate,
                    Fixable: false,
                    Metadata: new Dictionary<string, string>
                    {
                        ["resolvedVersion"] = sample.ResolvedVersion,
                        ["alternativePackage"] = sample.AlternativePackage ?? string.Empty,
                        ["alternativeVersionRange"] = sample.AlternativeVersionRange ?? string.Empty
                    });
            })
            .ToList();

        return new AnalyzerResult(Name, issues);
    }
}
