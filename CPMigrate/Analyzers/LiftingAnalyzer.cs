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

        foreach (var projectPath in packageInfo.References.Select(r => r.ProjectPath).Distinct())
        {
            var projectId = packageInfo.ProjectId(projectPath);
            var redundant = _graphService.IdentifyRedundantDirectReferences(projectPath);
            foreach (var packageName in redundant)
            {
                issues.Add(new AnalysisIssue(
                    packageName,
                    $"Direct reference is redundant; it is already provided transitively by another top-level package in {projectId}.",
                    [projectId],
                    AnalysisIssueCode.RedundantDirectReference,
                    AnalysisSeverity.Low,
                    Fixable: false
                ));
            }
        }

        return new AnalyzerResult(Name, issues);
    }
}
