using System.Text.Json;
using CPMigrate.Models;
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

    /// <inheritdoc />
    public ProjectResolvedGraph? TryReadResolvedGraph(string projectFilePath)
    {
        var assetsPath = GetAssetsPath(projectFilePath);

        if (!File.Exists(assetsPath))
        {
            return null;
        }

        try
        {
            using var doc = ReadAssetsDocument(assetsPath);

            if (!DescribesProject(doc, projectFilePath))
            {
                return null;
            }

            return BuildResolvedGraph(projectFilePath, doc);
        }
        catch (Exception ex)
            when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _console.Warning(
                $"Could not read the resolved graph for {Path.GetFileName(projectFilePath)}: {ex.Message}"
            );
            return null;
        }
    }

    /// <summary>
    /// Whether this assets file is actually about this project.
    /// </summary>
    /// <remarks>
    /// It reads its own subject: NuGet records the project it restored as
    /// <c>project.restore.projectPath</c>. Asking is the only way to tell a current graph from a
    /// plausible one, and two situations produce a plausible one.
    ///
    /// <para>A project that redirects its intermediate output writes its real graph elsewhere while a
    /// stale <c>obj/project.assets.json</c> from before the redirect can still sit here — read
    /// happily, compared against itself in both snapshots, and reported unchanged over a migration
    /// that changed the build. Cross-review caught it: failing closed on the file being *absent* was
    /// never the same as failing closed on it being *wrong*.</para>
    ///
    /// <para>And two projects in one directory share this path, so whichever restored last answers
    /// for both.</para>
    ///
    /// An assets file that does not say what it describes is trusted rather than rejected — older
    /// NuGet versions omit the field, and refusing every project on those would make verification
    /// unavailable rather than careful.
    /// </remarks>
    private static bool DescribesProject(JsonDocument doc, string projectFilePath)
    {
        if (
            !doc.RootElement.TryGetProperty("project", out var project)
            || !project.TryGetProperty("restore", out var restore)
            || !restore.TryGetProperty("projectPath", out var recorded)
        )
        {
            return true;
        }

        var recordedPath = recorded.GetString();

        if (string.IsNullOrWhiteSpace(recordedPath))
        {
            return true;
        }

        return string.Equals(
            Path.GetFullPath(recordedPath),
            Path.GetFullPath(projectFilePath),
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Builds the graph from the two sections that describe it, driven by the frameworks the *project*
    /// declares rather than by whatever keys <c>targets</c> happens to carry.
    ///
    /// That direction matters twice. It is what lets a declared framework restore did not write be
    /// reported as unresolved rather than as empty. And it skips the RID-qualified target keys
    /// (<c>net10.0/win-x64</c>) that appear alongside the plain one when a project declares runtime
    /// identifiers — counting those would multiply every package by the RID count and turn adding a RID
    /// into hundreds of phantom additions.
    /// </summary>
    private static ProjectResolvedGraph? BuildResolvedGraph(
        string projectFilePath,
        JsonDocument doc
    )
    {
        if (
            !doc.RootElement.TryGetProperty("project", out var projectNode)
            || !projectNode.TryGetProperty("frameworks", out var frameworksNode)
            || !doc.RootElement.TryGetProperty("targets", out var targetsNode)
        )
        {
            return null;
        }

        List<ResolvedFramework> frameworks = [];

        foreach (var framework in frameworksNode.EnumerateObject())
        {
            var direct = new HashSet<string>(
                ReadDirectPackages(framework.Value).Keys,
                StringComparer.OrdinalIgnoreCase
            );

            if (!targetsNode.TryGetProperty(framework.Name, out var targetNode))
            {
                frameworks.Add(new ResolvedFramework(framework.Name, Resolved: false, []));
                continue;
            }

            frameworks.Add(
                new ResolvedFramework(
                    framework.Name,
                    Resolved: true,
                    ReadResolvedPackages(targetNode, direct)
                )
            );
        }

        // Sorted so two runs over the same tree produce byte-identical reports whatever order the
        // document enumerated in.
        return new ProjectResolvedGraph(
            projectFilePath,
            [.. frameworks.OrderBy(f => f.TargetFramework, StringComparer.Ordinal)]
        );
    }

    private static List<ResolvedPackage> ReadResolvedPackages(
        JsonElement targetNode,
        HashSet<string> direct
    )
    {
        List<ResolvedPackage> packages = [];

        foreach (var entry in targetNode.EnumerateObject())
        {
            // A ProjectReference appears here as "type": "project", carrying the referenced project's
            // version rather than a package version. Counting one would make every version bump of a
            // sibling project read as dependency drift.
            if (
                entry.Value.TryGetProperty("type", out var type)
                && !string.Equals(
                    type.GetString(),
                    PackageTarget,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var separator = entry.Name.LastIndexOf('/');
            if (separator < 0)
            {
                continue;
            }

            var id = entry.Name[..separator];
            packages.Add(
                new ResolvedPackage(
                    id,
                    NormalizeVersion(entry.Name[(separator + 1)..]),
                    direct.Contains(id),
                    ReadDependencyIds(entry.Value)
                )
            );
        }

        return [.. packages.OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The package IDs one resolved node pulls in. Only the IDs: the versions on these edges are
    /// requirement ranges, and what each requirement resolved to is already recorded as its own node.
    /// </summary>
    private static IReadOnlyList<string> ReadDependencyIds(JsonElement node)
    {
        if (!node.TryGetProperty("dependencies", out var dependencies))
        {
            return [];
        }

        return [.. dependencies.EnumerateObject().Select(d => d.Name)];
    }

    /// <summary>
    /// Settles two spellings of one release onto a single form, through the shared normalizer.
    ///
    /// Two restores of the same tree can write <c>4.3</c>, <c>4.3.0</c>, or <c>4.3.0+build.5</c> for the
    /// same package. Comparing the raw strings would manufacture drift nothing caused, and a report that
    /// cries wolf is one nobody reads. Shared with the recorded migration decisions rather than
    /// duplicated: normalizing only one side of a comparison is worse than normalizing neither.
    /// </summary>
    private static string NormalizeVersion(string raw) => VersionText.Normalize(raw);

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
