using System.Text.Json;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Service to analyze the full dependency graph using project.assets.json.
/// </summary>
public class DependencyGraphService
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
        var redundant = new List<string>();
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
                var directDeps = new Dictionary<string, string>();
                if (framework.Value.TryGetProperty("dependencies", out var depsNode))
                {
                    foreach (var dep in depsNode.EnumerateObject())
                    {
                        directDeps[dep.Name] = dep.Value.GetProperty("version").GetString() ?? "";
                    }
                }

                // Now find the targets for this framework to see transitive deps
                var targetFramework = framework.Name;
                if (doc.RootElement.GetProperty("targets").TryGetProperty(targetFramework, out var targetNode))
                {
                    // Map of package -> its transitive dependencies
                    var transitiveClosure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // For each direct dependency, find what it brings in
                    foreach (var directDep in directDeps.Keys)
                    {
                        CollectTransitiveRecursive(targetNode, directDep, directDeps[directDep], transitiveClosure, new HashSet<string>());
                    }

                    // Check if any direct dependency is in the transitive closure of OTHER direct dependencies
                    foreach (var directDep in directDeps.Keys)
                    {
                        // We need to check if it's brought in by ANY OTHER direct dep
                        var otherTransitiveClosure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var otherDep in directDeps.Keys.Where(k => k != directDep))
                        {
                            CollectTransitiveRecursive(targetNode, otherDep, directDeps[otherDep], otherTransitiveClosure, new HashSet<string>());
                        }

                        if (otherTransitiveClosure.Contains(directDep))
                        {
                            redundant.Add(directDep);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _console.Warning($"Could not analyze dependency graph for {Path.GetFileName(projectFilePath)}: {ex.Message}");
        }

        return redundant.Distinct().ToList();
    }

    private void CollectTransitiveRecursive(JsonElement targetNode, string package, string version, HashSet<string> closure, HashSet<string> visited)
    {
        var key = $"{package}/{version}";
        if (!visited.Add(key)) return;

        if (targetNode.TryGetProperty(key, out var packageNode))
        {
            if (packageNode.TryGetProperty("dependencies", out var depsNode))
            {
                foreach (var dep in depsNode.EnumerateObject())
                {
                    closure.Add(dep.Name);
                    CollectTransitiveRecursive(targetNode, dep.Name, dep.Value.GetString() ?? "", closure, visited);
                }
            }
        }
    }
}
