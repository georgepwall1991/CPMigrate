using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes projects for direct package references that are already provided transitively.
/// </summary>
public class LiftingAnalyzer : IAnalyzer
{
    private readonly IDependencyGraphService _graphService;

    public LiftingAnalyzer(IDependencyGraphService graphService)
    {
        _graphService = graphService;
    }

    public string Name => "Redundant Direct References (Lifting)";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        List<AnalysisIssue> issues = [];

        foreach (var reference in packageInfo.References.DistinctBy(r => r.ProjectPath))
        {
            var redundant = _graphService.IdentifyRedundantDirectReferences(reference.ProjectPath);
            foreach (var packageName in redundant)
            {
                issues.Add(new AnalysisIssue(
                    packageName,
                    $"Direct reference is redundant; it is already provided transitively by another top-level package in {reference.ProjectName}.",
                    [reference.ProjectName],
                    AnalysisIssueCode.RedundantDirectReference,
                    AnalysisSeverity.Low,
                    Fixable: false
                ));
            }
        }

        return new AnalyzerResult(Name, issues);
    }
}
