using System.Text.Json;
using NuGet.Versioning;

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

    /// <summary>
    /// What one target framework says about a package: whether the project references it directly there,
    /// and whether dropping that reference would be safe.
    /// </summary>
    private sealed record FrameworkVerdict(
        HashSet<string> DirectlyReferenced,
        HashSet<string> SafeToDrop
    );

    /// <summary>
    /// Reports a reference only when *every* target framework that references it directly agrees it is
    /// safe to drop.
    ///
    /// Unioning per-framework findings would advise removing a reference that is transitive under
    /// <c>net10.0</c> but independently required under <c>netstandard2.0</c> — and the advice would look
    /// just as confident as a correct one. A framework declared by the project but absent from
    /// <c>targets</c> cannot be judged at all, so a reference it declares is not reported either: the cost
    /// of staying quiet is a missed finding, and the cost of guessing is a broken restore.
    /// </summary>
    private static List<string> AnalyzeAssetsDocument(JsonDocument doc)
    {
        if (
            !doc.RootElement.TryGetProperty("project", out var projectNode)
            || !projectNode.TryGetProperty("frameworks", out var frameworksNode)
            || !doc.RootElement.TryGetProperty("targets", out var targetsNode)
        )
        {
            return [];
        }

        List<FrameworkVerdict> verdicts = [];

        foreach (var framework in frameworksNode.EnumerateObject())
        {
            var directPackages = ReadDirectPackages(framework.Value);

            if (!targetsNode.TryGetProperty(framework.Name, out var targetNode))
            {
                // Unjudgeable: record what it references so nothing here can be reported.
                verdicts.Add(new FrameworkVerdict([.. directPackages.Keys], []));
                continue;
            }

            verdicts.Add(AnalyzeFramework(directPackages, targetNode));
        }

        var everywhere = verdicts
            .SelectMany(verdict => verdict.DirectlyReferenced)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(package =>
                verdicts
                    .Where(verdict => verdict.DirectlyReferenced.Contains(package))
                    .All(verdict => verdict.SafeToDrop.Contains(package))
            )
            .ToList();

        return everywhere;
    }

    private static FrameworkVerdict AnalyzeFramework(
        Dictionary<string, VersionRange?> directPackages,
        JsonElement targetNode
    )
    {
        HashSet<string> directlyReferenced = new(
            directPackages.Keys,
            StringComparer.OrdinalIgnoreCase
        );

        if (directPackages.Count < 2)
        {
            // One reference cannot be made redundant by another.
            return new FrameworkVerdict(directlyReferenced, []);
        }

        var resolved = IndexByPackageName(targetNode);
        HashSet<string> safeToDrop = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (package, declaredRange) in directPackages)
        {
            if (
                IsSafelyProvidedByAnotherDirectReference(
                    package,
                    declaredRange,
                    directPackages,
                    resolved
                )
            )
            {
                safeToDrop.Add(package);
            }
        }

        return new FrameworkVerdict(directlyReferenced, safeToDrop);
    }

    /// <summary>
    /// The project's own top-level package references, with the range each one declares.
    ///
    /// A ProjectReference also appears here, with <c>"target": "Project"</c> and no version. Removing one
    /// because another project happens to reference it too is a different question from package
    /// redundancy, and not one this analyzer is entitled to answer.
    /// </summary>
    private static Dictionary<string, VersionRange?> ReadDirectPackages(JsonElement frameworkNode)
    {
        Dictionary<string, VersionRange?> direct = new(StringComparer.OrdinalIgnoreCase);

        if (!frameworkNode.TryGetProperty("dependencies", out var dependencies))
        {
            return direct;
        }

        foreach (var dependency in dependencies.EnumerateObject())
        {
            var isPackage =
                !dependency.Value.TryGetProperty("target", out var target)
                || string.Equals(
                    target.GetString(),
                    PackageTarget,
                    StringComparison.OrdinalIgnoreCase
                );

            if (isPackage)
            {
                direct[dependency.Name] = ReadRange(dependency.Value);
            }
        }

        return direct;
    }

    private static VersionRange? ReadRange(JsonElement dependencyNode)
    {
        if (!dependencyNode.TryGetProperty("version", out var version))
        {
            return null;
        }

        return VersionRange.TryParse(version.GetString() ?? string.Empty, out var parsed)
            ? parsed
            : null;
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
    /// identifies a node and no version needs reconstructing to find it.
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

    /// <summary>
    /// Whether another direct reference already brings this package in <em>at a version that satisfies what
    /// the direct reference asks for</em>.
    ///
    /// Reachability alone is not enough, and getting this wrong produces advice that breaks a build. Take
    /// a project referencing Serilog.Sinks.File 7.0.0 and Serilog 4.3.0 directly: the sink only requires
    /// Serilog 4.2.0, and restore settled on 4.3.0 *because of the direct reference*. Serilog is reachable,
    /// so reachability calls the reference redundant — but removing it silently downgrades Serilog to
    /// 4.2.0. The finding would read as a tidy-up and land as a regression.
    ///
    /// So the question is whether the version that would be resolved *without* this reference — the highest
    /// any other package requires — still satisfies the range the reference declares. That is asked of the
    /// range itself rather than by comparing floors, because a floor comparison cannot see the difference
    /// between <c>[4.3.0, )</c> and <c>(4.3.0, )</c>: both report a minimum of 4.3.0, so a provider
    /// requiring exactly 4.3.0 looked sufficient for a reference that excludes it. Asking the range also
    /// gets exact pins and upper bounds right for free. Where the range cannot be established the reference
    /// is left alone.
    /// </summary>
    private static bool IsSafelyProvidedByAnotherDirectReference(
        string package,
        VersionRange? declaredRange,
        Dictionary<string, VersionRange?> directPackages,
        Dictionary<string, JsonElement> resolved
    )
    {
        if (declaredRange is null)
        {
            // An absent or unparseable range. Nothing safe can be concluded.
            return false;
        }

        Dictionary<string, NuGetVersion> highestRequired = new(StringComparer.OrdinalIgnoreCase);

        foreach (
            var other in directPackages.Keys.Where(candidate =>
                !string.Equals(candidate, package, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            CollectRequirements(other, resolved, highestRequired);
        }

        return highestRequired.TryGetValue(package, out var wouldResolveTo)
            && declaredRange.Satisfies(wouldResolveTo);
    }

    /// <summary>
    /// Walks everything reachable from a package, recording the highest version each dependency is
    /// required at along the way. The recorded set doubles as the visited set, so a cycle — which
    /// malformed or mutually-referencing packages do produce — terminates.
    /// </summary>
    private static void CollectRequirements(
        string package,
        Dictionary<string, JsonElement> resolved,
        Dictionary<string, NuGetVersion> highestRequired
    )
    {
        if (
            !resolved.TryGetValue(package, out var node)
            || !node.TryGetProperty("dependencies", out var dependencies)
        )
        {
            return;
        }

        foreach (var dependency in dependencies.EnumerateObject())
        {
            var required = VersionRange.TryParse(
                dependency.Value.GetString() ?? string.Empty,
                out var range
            )
                ? range.MinVersion
                : null;

            var isNew = !highestRequired.TryGetValue(dependency.Name, out var known);

            if (required is not null && (known is null || required > known))
            {
                highestRequired[dependency.Name] = required;
            }

            // Recurse on first sight only. Revisiting on a raised version would loop on a cycle, and a
            // package's own dependencies do not change with the version another package asks for.
            if (isNew)
            {
                if (required is null)
                {
                    // Still mark it seen, so an unparseable constraint cannot cause repeated traversal.
                    highestRequired[dependency.Name] = NuGetVersion.Parse("0.0.0");
                }

                CollectRequirements(dependency.Name, resolved, highestRequired);
            }
        }
    }
}
