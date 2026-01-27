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

                // Analyze Properties
                foreach (var propertyGroup in projectRoot.PropertyGroups)
                {
                    // Skip conditional property groups for now to be safe
                    if (!string.IsNullOrEmpty(propertyGroup.Condition))
                    {
                        continue;
                    }

                    foreach (var property in propertyGroup.Properties)
                    {
                        if (IgnoredProperties.Contains(property.Name))
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(property.Condition))
                        {
                            continue; // Skip conditional properties
                        }

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

                // Analyze Items (Currently only "Using")
                foreach (var itemGroup in projectRoot.ItemGroups)
                {
                    if (!string.IsNullOrEmpty(itemGroup.Condition))
                    {
                        continue;
                    }

                    foreach (var item in itemGroup.Items)
                    {
                        if (item.ItemType != "Using" && item.ItemType != "PackageReference")
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(item.Condition))
                        {
                            continue;
                        }

                        // Create metadata dictionary
                        var metadata = item.Metadata.ToDictionary(m => m.Name, m => m.Value);

                        // Create a unique key for the item
                        // Format: Type|Include|MetadataKey=MetadataValue;...
                        var metadataString = string.Join(";", metadata.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}"));
                        var key = $"{item.ItemType}|{item.Include}|{metadataString}";

                        if (!result.ItemOccurrences.ContainsKey(key))
                        {
                            result.ItemOccurrences[key] = new List<ProjectItem>();
                        }

                        result.ItemOccurrences[key].Add(new ProjectItem(
                            item.ItemType,
                            item.Include,
                            path,
                            metadata
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
