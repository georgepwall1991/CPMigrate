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
    public List<string> DetectConflicts(Dictionary<string, HashSet<string>> packageVersions)
    {
        return packageVersions
            .Where(kvp => kvp.Value.Count > 1)
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
            // Should not happen given caller context, but return something safe
            return "0.0.0";
        }

        // If there's only one version, return it as-is (preserves floating versions like 1.0.*)
        if (versionList.Count == 1)
        {
            return versionList[0];
        }

        // Parse versions using NuGetVersion, tracking original strings for fallback
        var parsedVersions = new List<(NuGetVersion? Parsed, string Original)>();

        foreach (var v in versionList)
        {
            if (NuGetVersion.TryParse(v, out var nuVer))
            {
                parsedVersions.Add((nuVer, v));
            }
            else
            {
                // Log warning for invalid/floating version format but preserve original
                _consoleService?.Warning($"Non-standard version format '{v}' - cannot compare, preserving as-is");
                parsedVersions.Add((null, v));
            }
        }

        // Separate parseable and unparseable versions
        var parseableVersions = parsedVersions
            .Where(p => p.Parsed != null)
            .OrderBy(p => p.Parsed)
            .ToList();

        var unparseableVersions = parsedVersions
            .Where(p => p.Parsed == null)
            .Select(p => p.Original)
            .ToList();

        // If no versions could be parsed, return the first original version
        if (parseableVersions.Count == 0)
        {
            _consoleService?.Warning($"No valid versions to compare - using first version: {versionList[0]}");
            return versionList[0];
        }

        // Warn about unparseable versions that won't be considered in comparison
        if (unparseableVersions.Count > 0)
        {
            _consoleService?.Warning($"Skipping {unparseableVersions.Count} non-standard version(s) in comparison: {string.Join(", ", unparseableVersions)}");
        }

        var selectedVersion = strategy switch
        {
            ConflictStrategy.Highest => parseableVersions.Last(),
            ConflictStrategy.Lowest => parseableVersions.First(),
            ConflictStrategy.Fail => parseableVersions.Last(), // Should be handled by caller logic
            _ => parseableVersions.Last()
        };

        return selectedVersion.Parsed!.ToNormalizedString();
    }
}