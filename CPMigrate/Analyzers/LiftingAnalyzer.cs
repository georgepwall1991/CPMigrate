using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes projects for direct package references that are already provided transitively.
/// </summary>
public class LiftingAnalyzer : IAnalyzer
{
    private readonly DependencyGraphService _graphService;

    public LiftingAnalyzer(DependencyGraphService graphService)
    {
        _graphService = graphService;
    }

    public string Name => "Redundant Direct References (Lifting)";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();
        var processedProjects = new HashSet<string>();

        foreach (var reference in packageInfo.References)
        {
            if (processedProjects.Add(reference.ProjectPath))
            {
                var redundant = _graphService.IdentifyRedundantDirectReferences(reference.ProjectPath);
                foreach (var packageName in redundant)
                {
                    issues.Add(new AnalysisIssue(
                        packageName,
                        $"Direct reference is redundant; it is already provided transitively by another top-level package in {reference.ProjectName}.",
                        new List<string> { reference.ProjectName }
                    ));
                }
            }
        }

        return new AnalyzerResult(Name, issues);
    }
}
