using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Reports packages that have newer versions available.
/// </summary>
public class OutdatedPackageAnalyzer : IAnalyzer
{
    public string Name => "Outdated Packages";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        if (packageInfo.OutdatedPackages == null || packageInfo.OutdatedPackages.Count == 0)
        {
            return new AnalyzerResult(Name, []);
        }

        var issues = packageInfo.OutdatedPackages
            .GroupBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sample = group.First();
                var affectedProjects = group.Select(p => packageInfo.ProjectId(p.ProjectPath)).Distinct().ToList();
                var latestVersions = group.Select(p => p.LatestVersion).Distinct().OrderBy(v => v).ToList();
                var description = $"Resolved: {sample.ResolvedVersion}, Latest: {string.Join(", ", latestVersions)}";

                return new AnalysisIssue(
                    sample.PackageName,
                    description,
                    affectedProjects,
                    AnalysisIssueCode.OutdatedPackage,
                    AnalysisSeverity.Low,
                    Fixable: false,
                    Metadata: new Dictionary<string, string>
                    {
                        ["resolvedVersion"] = sample.ResolvedVersion,
                        ["latestVersion"] = latestVersions.FirstOrDefault() ?? sample.LatestVersion
                    });
            })
            .ToList();

        return new AnalyzerResult(Name, issues);
    }
}
