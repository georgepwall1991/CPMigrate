using CPMigrate.Models;
using Microsoft.Build.Construction;

namespace CPMigrate.Services;

public class BuildPropsAnalyzer
{
    private readonly IConsoleService _consoleService;

    // Properties that are typically unique to a project and should not be moved to Directory.Build.props
    private static readonly HashSet<string> IgnoredProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProjectGuid",
        "AssemblyName",
        "RootNamespace",
        "BaseOutputPath",
        "IntermediateOutputPath",
        "DocumentationFile",
        "ApplicationIcon",
        "Win32Resource",
        "SignAssembly",
        "AssemblyOriginatorKeyFile"
    };

    public BuildPropsAnalyzer(IConsoleService consoleService)
    {
        _consoleService = consoleService;
    }

    public PropertyAnalysisResult Analyze(List<string> projectPaths)
    {
        var result = new PropertyAnalysisResult
        {
            TotalProjects = projectPaths.Count
        };

        foreach (var path in projectPaths)
        {
            try
            {
                // Load the project as XML only, no evaluation
                var projectRoot = ProjectRootElement.Open(path);

                foreach (var propertyGroup in projectRoot.PropertyGroups)
                {
                    // Skip conditional property groups for now to be safe
                    if (!string.IsNullOrEmpty(propertyGroup.Condition)) continue;

                    foreach (var property in propertyGroup.Properties)
                    {
                        if (IgnoredProperties.Contains(property.Name)) continue;
                        if (!string.IsNullOrEmpty(property.Condition)) continue; // Skip conditional properties

                        var key = $"{property.Name}|{property.Value}";
                        
                        if (!result.PropertyOccurrences.ContainsKey(key))
                        {
                            result.PropertyOccurrences[key] = new List<ProjectProperty>();
                        }

                        result.PropertyOccurrences[key].Add(new ProjectProperty(
                            property.Name,
                            property.Value,
                            path
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                _consoleService.Warning($"Failed to analyze properties for {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return result;
    }
}
