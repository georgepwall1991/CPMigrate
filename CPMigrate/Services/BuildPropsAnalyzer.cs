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
                var projectRoot = ProjectRootElement.Open(path);
                AnalyzeProjectProperties(projectRoot, path, result);
                AnalyzeProjectItems(projectRoot, path, result);
            }
            catch (Exception ex)
            {
                _consoleService.Warning($"Failed to analyze properties for {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return result;
    }

    private static void AnalyzeProjectProperties(
        ProjectRootElement projectRoot,
        string projectPath,
        PropertyAnalysisResult result)
    {
        foreach (var propertyGroup in projectRoot.PropertyGroups)
        {
            if (!string.IsNullOrEmpty(propertyGroup.Condition))
            {
                continue; // Skip conditional property groups
            }

            foreach (var property in propertyGroup.Properties)
            {
                if (ShouldSkipProperty(property))
                {
                    continue;
                }

                var key = $"{property.Name}|{property.Value}";

                if (!result.PropertyOccurrences.ContainsKey(key))
                {
                    result.PropertyOccurrences[key] = new List<ProjectProperty>();
                }

                result.PropertyOccurrences[key].Add(new ProjectProperty(
                    property.Name,
                    property.Value,
                    projectPath
                ));
            }
        }
    }

    private static bool ShouldSkipProperty(ProjectPropertyElement property)
    {
        return IgnoredProperties.Contains(property.Name) ||
               !string.IsNullOrEmpty(property.Condition);
    }

    private static void AnalyzeProjectItems(
        ProjectRootElement projectRoot,
        string projectPath,
        PropertyAnalysisResult result)
    {
        foreach (var itemGroup in projectRoot.ItemGroups)
        {
            if (!string.IsNullOrEmpty(itemGroup.Condition))
            {
                continue; // Skip conditional item groups
            }

            foreach (var item in itemGroup.Items)
            {
                if (ShouldSkipItem(item))
                {
                    continue;
                }

                var metadata = item.Metadata.ToDictionary(m => m.Name, m => m.Value);
                var key = CreateItemKey(item, metadata);

                if (!result.ItemOccurrences.ContainsKey(key))
                {
                    result.ItemOccurrences[key] = new List<ProjectItem>();
                }

                result.ItemOccurrences[key].Add(new ProjectItem(
                    item.ItemType,
                    item.Include,
                    projectPath,
                    metadata
                ));
            }
        }
    }

    private static bool ShouldSkipItem(ProjectItemElement item)
    {
        return (item.ItemType != "Using" && item.ItemType != "PackageReference") ||
               !string.IsNullOrEmpty(item.Condition);
    }

    private static string CreateItemKey(ProjectItemElement item, Dictionary<string, string> metadata)
    {
        var metadataString = string.Join(";",
            metadata.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{item.ItemType}|{item.Include}|{metadataString}";
    }
}
