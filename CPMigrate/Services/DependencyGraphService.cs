using System.Text.Json;

namespace CPMigrate.Services;

/// <summary>
/// Service to analyze the full dependency graph using project.assets.json.
/// </summary>
public class DependencyGraphService : IDependencyGraphService
{
    /// <summary>Direct dependencies NuGet resolves from the feed, as opposed to project references.</summary>
    private const string PackageTarget = "Package";

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
        var assetsPath = GetAssetsPath(projectFilePath);

        if (!File.Exists(assetsPath))
        {
            return [];
        }

        try
        {
            using var doc = ReadAssetsDocument(assetsPath);
            return AnalyzeAssetsDocument(doc);
        }
        catch (Exception ex)
        {
            _console.Warning(
                $"Could not analyze dependency graph for {Path.GetFileName(projectFilePath)}: {ex.Message}"
            );
            return [];
        }
    }

    private static string GetAssetsPath(string projectFilePath)
    {
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? ".";
        return Path.Combine(projectDir, "obj", "project.assets.json");
    }

    private static JsonDocument ReadAssetsDocument(string assetsPath)
    {
        var json = File.ReadAllText(assetsPath);
        return JsonDocument.Parse(json);
    }

    private static List<string> AnalyzeAssetsDocument(JsonDocument doc)
    {
        List<string> redundant = [];

        if (
            !doc.RootElement.TryGetProperty("project", out var projectNode)
            || !projectNode.TryGetProperty("frameworks", out var frameworksNode)
            || !doc.RootElement.TryGetProperty("targets", out var targetsNode)
        )
        {
            return redundant;
        }

        foreach (var framework in frameworksNode.EnumerateObject())
        {
            if (targetsNode.TryGetProperty(framework.Name, out var targetNode))
            {
                redundant.AddRange(AnalyzeFramework(framework.Value, targetNode));
            }
        }

        return redundant.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> AnalyzeFramework(JsonElement frameworkNode, JsonElement targetNode)
    {
        var directPackages = ReadDirectPackages(frameworkNode);
        if (directPackages.Count < 2)
        {
            // One reference cannot be made redundant by another.
            return [];
        }

        var resolved = IndexByPackageName(targetNode);

        return directPackages
            .Where(package => IsProvidedByAnotherDirectReference(package, directPackages, resolved))
            .ToList();
    }

    /// <summary>
    /// The project's own top-level references, package ones only.
    ///
    /// A ProjectReference also appears here, with <c>"target": "Project"</c> and no version. Removing one
    /// because another project happens to reference it too is a different question from package
    /// redundancy, and not one this analyzer is entitled to answer.
    /// </summary>
    private static List<string> ReadDirectPackages(JsonElement frameworkNode)
    {
        if (!frameworkNode.TryGetProperty("dependencies", out var dependencies))
        {
            return [];
        }

        return dependencies
            .EnumerateObject()
            .Where(dependency =>
                !dependency.Value.TryGetProperty("target", out var target)
                || string.Equals(
                    target.GetString(),
                    PackageTarget,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(dependency => dependency.Name)
            .ToList();
    }

    /// <summary>
    /// The resolved graph, keyed by package name alone.
    ///
    /// This is the correction that made the analyzer work at all. <c>targets</c> is keyed by
    /// <c>Name/ResolvedVersion</c>, but the version in <c>project.frameworks.&lt;tf&gt;.dependencies</c> is
    /// a *range* — NuGet writes <c>"[7.0.0, )"</c> for an ordinary reference. Composing a key from that
    /// range produced <c>Serilog/[7.0.0, )</c>, which matches nothing in any real assets file, so the
    /// traversal never found a single dependency and the analyzer reported nothing on every project it was
    /// ever run against. It looked like a clean result rather than a broken lookup.
    ///
    /// Restore settles on exactly one version per package per target framework, so the name alone
    /// identifies a node and no version needs parsing, comparing, or reconstructing.
    /// </summary>
    private static Dictionary<string, JsonElement> IndexByPackageName(JsonElement targetNode)
    {
        Dictionary<string, JsonElement> resolved = new(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in targetNode.EnumerateObject())
        {
            var separator = entry.Name.LastIndexOf('/');
            var name = separator < 0 ? entry.Name : entry.Name[..separator];
            resolved[name] = entry.Value;
        }

        return resolved;
    }

    private static bool IsProvidedByAnotherDirectReference(
        string package,
        List<string> directPackages,
        Dictionary<string, JsonElement> resolved
    )
    {
        HashSet<string> reachable = new(StringComparer.OrdinalIgnoreCase);

        foreach (
            var other in directPackages.Where(candidate =>
                !string.Equals(candidate, package, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            CollectReachable(other, resolved, reachable);
        }

        return reachable.Contains(package);
    }

    /// <summary>
    /// Everything reachable from a package, excluding the package itself. The visited set is the same
    /// collection, so a cycle — which malformed or mutually-referencing packages do produce — terminates.
    /// </summary>
    private static void CollectReachable(
        string package,
        Dictionary<string, JsonElement> resolved,
        HashSet<string> reachable
    )
    {
        if (
            !resolved.TryGetValue(package, out var node)
            || !node.TryGetProperty("dependencies", out var dependencies)
        )
        {
            return;
        }

        foreach (
            var dependency in dependencies
                .EnumerateObject()
                .Where(dependency => reachable.Add(dependency.Name))
        )
        {
            CollectReachable(dependency.Name, resolved, reachable);
        }
    }
}
