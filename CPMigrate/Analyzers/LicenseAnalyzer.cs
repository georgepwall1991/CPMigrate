using CPMigrate.Licensing;
using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Reports copyleft, proprietary, and unknown licenses from <see cref="ProjectPackageInfo.Licenses"/>.
/// Absent scan data (no <c>--licenses</c>) is silence, not a hardcoded package-name guess.
/// </summary>
internal sealed class LicenseAnalyzer : IAnalyzer
{
    public string Name => "Package Licenses";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        if (packageInfo.Licenses is null || packageInfo.Licenses.Count == 0)
        {
            return new AnalyzerResult(Name, []);
        }

        var issues = packageInfo
            .Licenses.GroupBy(license => license.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToIssue(group, packageInfo))
            .Where(issue => issue is not null)
            .Select(issue => issue!)
            .ToList();

        return new AnalyzerResult(Name, issues);
    }

    private static AnalysisIssue? ToIssue(
        IGrouping<string, LicenseInfo> group,
        ProjectPackageInfo packageInfo
    )
    {
        var worst = group.MaxBy(license => license.Classification);
        if (worst is null || worst.Classification == LicenseClassification.Permissive)
        {
            return null;
        }

        var (severity, risk, description) = Describe(worst);
        var projects = group
            .Select(license => packageInfo.ProjectId(license.ProjectPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AnalysisIssue(
            worst.PackageName,
            description,
            projects,
            AnalysisIssueCode.LicenseRisk,
            severity,
            Fixable: false,
            Metadata: new Dictionary<string, string>
            {
                ["license"] = worst.License,
                ["risk"] = risk,
                ["licenseType"] = worst.LicenseType,
            }
        );
    }

    private static (AnalysisSeverity Severity, string Risk, string Description) Describe(LicenseInfo license)
    {
        return license.Classification switch
        {
            LicenseClassification.StrongCopyleft => (
                AnalysisSeverity.High,
                "copyleft",
                $"{license.License} license — copyleft; derivative works must use the same license"
            ),
            LicenseClassification.WeakCopyleft => (
                AnalysisSeverity.Moderate,
                "copyleft",
                $"{license.License} license — weak copyleft; review linking and distribution terms"
            ),
            LicenseClassification.Proprietary => (
                AnalysisSeverity.Moderate,
                "proprietary",
                $"{license.License} license — proprietary; review terms before distribution"
            ),
            _ => (
                AnalysisSeverity.Low,
                "unknown",
                $"{license.License} license — unverified; review the package license before shipping"
            ),
        };
    }
}
