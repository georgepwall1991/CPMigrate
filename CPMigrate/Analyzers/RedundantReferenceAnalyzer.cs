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
        List<AnalysisIssue> issues = [];

        // Grouped from the references as *declared*, not as resolved. Resolution collapses two
        // PackageReference items with the same Include into one, so reading the resolved list meant this
        // rule could never see a duplicate and never reported one.
        //
        // Conditional declarations are excluded. Declaring a package once per target framework, each
        // behind a Condition, is how multi-targeting is written — and since this finding is fixable,
        // calling it a duplicate would have the fixer delete the declaration another framework depends on.
        // A rule that quietly reported nothing became a rule that breaks a build, which is the worse of
        // the two. MSBuild conditions cannot be evaluated reliably outside a build, so overlap is not
        // guessed at: a duplicate is reported only among declarations that always apply.
        var projectGroups = packageInfo
            .GetDeclaredReferences()
            .Where(reference => !reference.IsConditional)
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
                if (firstRef == null)
                {
                    continue;
                }

                var projectName = packageInfo.ProjectId(firstRef.ProjectPath);
                var count = packageGroup.Count();
                var versions = packageGroup.Select(r => r.Version).Distinct().ToList();

                var description = versions.Count == 1
                    ? $"Referenced {count} times with version {versions[0]}"
                    : $"Referenced {count} times with versions: {string.Join(", ", versions)}";

                issues.Add(new AnalysisIssue(
                    packageGroup.Key,
                    description,
                    [projectName],
                    AnalysisIssueCode.RedundantReference,
                    AnalysisSeverity.Low,
                    Fixable: true
                ));
            }
        }

        return new AnalyzerResult(Name, issues);
    }
}
