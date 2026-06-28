using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Handles version conflict detection and resolution using NuGet.Versioning logic.
/// </summary>
public class VersionResolver
{
    private readonly IConsoleService? _consoleService;

    public VersionResolver(IConsoleService? consoleService = null)
    {
        _consoleService = consoleService;
    }

    /// <summary>
    /// Detects packages that have multiple versions across projects.
    /// </summary>
    /// <param name="packageVersions">Dictionary mapping package names to their version sets.</param>
    /// <returns>List of package names that have version conflicts, sorted alphabetically.</returns>
    public static List<string> DetectConflicts(
        Dictionary<string, HashSet<string>> packageVersions,
        Dictionary<string, HashSet<string>>? existingPackageVersions = null)
    {
        return packageVersions
            .Where(kvp =>
            {
                if (kvp.Value.Count <= 1)
                {
                    return false;
                }

                // If we have multiple versions, check if they are already accounted for by conditions in existing props
                if (existingPackageVersions != null && existingPackageVersions.TryGetValue(kvp.Key, out var allowed) && kvp.Value.IsSubsetOf(allowed))
                {
                    return false;
                }

                return true;
            })
            .Select(kvp => kvp.Key)
            .OrderBy(name => name)
            .ToList();
    }

    /// <summary>
    /// Resolves a version conflict based on the specified strategy.
    /// </summary>
    /// <param name="versions">Collection of versions to choose from.</param>
    /// <param name="strategy">The resolution strategy.</param>
    /// <returns>The selected version based on the strategy.</returns>
    public string ResolveVersion(IEnumerable<string> versions, ConflictStrategy strategy)
    {
        var versionList = versions.ToList();

        if (versionList.Count == 0)
        {
            return "0.0.0";
        }

        if (versionList.Count == 1)
        {
            return versionList[0];
        }

        var (parseable, unparseable) = PartitionVersions(versionList);
        WarnAboutUnparseable(unparseable);

        if (parseable.Count == 0)
        {
            _consoleService?.Warning($"No valid versions to compare - using first version: {versionList[0]}");
            return versionList[0];
        }

        return SelectVersion(parseable, strategy);
    }

    private (List<NuGetVersion> Parseable, List<string> Unparseable) PartitionVersions(List<string> versions)
    {
        var parseable = new List<NuGetVersion>();
        var unparseable = new List<string>();

        foreach (var v in versions)
        {
            if (NuGetVersion.TryParse(v, out var nuVer))
            {
                parseable.Add(nuVer);
            }
            else
            {
                _consoleService?.Warning($"Non-standard version format '{v}' - cannot compare, preserving as-is");
                unparseable.Add(v);
            }
        }

        return (parseable, unparseable);
    }

    private void WarnAboutUnparseable(List<string> unparseable)
    {
        if (unparseable.Count > 0)
        {
            _consoleService?.Warning($"Skipping {unparseable.Count} non-standard version(s) in comparison: {string.Join(", ", unparseable)}");
        }
    }

    private static string SelectVersion(List<NuGetVersion> parseable, ConflictStrategy strategy)
    {
        var ordered = parseable.OrderBy(v => v).ToList();
        var selected = strategy == ConflictStrategy.Lowest
            ? ordered[0]
            : ordered[ordered.Count - 1];
        return selected.ToNormalizedString();
    }
}
