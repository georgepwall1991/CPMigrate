using System.Text.Json;

namespace CPMigrate.Services;

/// <summary>
/// Service to analyze the full dependency graph using project.assets.json.
/// </summary>
public class DependencyGraphService : IDependencyGraphService
{
    private readonly IConsoleService _console;

    public DependencyGraphService(IConsoleService console)
    {
        _console = console;
    }

    /// <summary>
    /// Identifies redundant direct references in a project.
    /// A reference is redundant if it's already provided transitively by another top-level package at the same or higher version.
    /// </summary>
    public List<string> IdentifyRedundantDirectReferences(string projectFilePath)
    {
        List<string> redundant = [];
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? ".";
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");

        if (!File.Exists(assetsPath))
        {
            return redundant;
        }

        try
        {
            var json = File.ReadAllText(assetsPath);
            using var doc = JsonDocument.Parse(json);

            var projectNode = doc.RootElement.GetProperty("project");
            var frameworksNode = projectNode.GetProperty("frameworks");

            foreach (var framework in frameworksNode.EnumerateObject())
            {
                AnalyzeFrameworkDependencies(doc, framework, redundant);
            }
        }
        catch (Exception ex)
        {
            _console.Warning($"Could not analyze dependency graph for {Path.GetFileName(projectFilePath)}: {ex.Message}");
        }

        return redundant.Distinct().ToList();
    }

    private void AnalyzeFrameworkDependencies(JsonDocument doc, JsonProperty framework, List<string> redundant)
    {
        Dictionary<string, string> directDeps = [];
        if (framework.Value.TryGetProperty("dependencies", out var depsNode))
        {
            foreach (var dep in depsNode.EnumerateObject())
            {
                directDeps[dep.Name] = dep.Value.GetProperty("version").GetString() ?? "";
            }
        }

        var targetFramework = framework.Name;
        if (doc.RootElement.GetProperty("targets").TryGetProperty(targetFramework, out var targetNode))
        {
            AnalyzeRedundancyForTarget(targetNode, directDeps, redundant);
        }
    }

    private void AnalyzeRedundancyForTarget(
        JsonElement targetNode,
        Dictionary<string, string> directDeps,
        List<string> redundant)
    {
        // Check if any direct dependency is in the transitive closure of OTHER direct dependencies
        redundant.AddRange(directDeps.Keys.Where(directDep => IsTransitiveDependencyOfOthers(targetNode, directDep, directDeps)));
    }

    private bool IsTransitiveDependencyOfOthers(
        JsonElement targetNode,
        string targetDep,
        Dictionary<string, string> directDeps)
    {
        // We need to check if it's brought in by ANY OTHER direct dep
        var otherTransitiveClosure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var otherDep in directDeps.Keys.Where(k => k != targetDep))
        {
            CollectTransitiveRecursive(targetNode, otherDep, directDeps[otherDep], otherTransitiveClosure, []);
        }

        return otherTransitiveClosure.Contains(targetDep);
    }

    private void CollectTransitiveRecursive(JsonElement targetNode, string package, string version, HashSet<string> closure, HashSet<string> visited)
    {
        var key = $"{package}/{version}";
        if (!visited.Add(key))
        {
            return;
        }

        if (targetNode.TryGetProperty(key, out var packageNode) &&
            packageNode.TryGetProperty("dependencies", out var depsNode))
        {
            foreach (var dep in depsNode.EnumerateObject())
            {
                closure.Add(dep.Name);
                CollectTransitiveRecursive(targetNode, dep.Name, dep.Value.GetString() ?? "", closure, visited);
            }
        }
    }
}
