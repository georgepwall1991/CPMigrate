namespace CPMigrate.Services;

/// <summary>
/// Handles version conflict detection and resolution.
/// </summary>
public class VersionResolver
{
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
        var sortedVersions = versions.OrderByDescending(ParseVersion).ToList();
        return strategy switch
        {
            ConflictStrategy.Highest => sortedVersions.First(),
            ConflictStrategy.Lowest => sortedVersions.Last(),
            ConflictStrategy.Fail => sortedVersions.First(), // Won't be used if Fail
            _ => sortedVersions.First()
        };
    }

    /// <summary>
    /// Parses a version string into a comparable Version object.
    /// Handles various version formats including prerelease tags.
    /// </summary>
    /// <param name="versionString">The version string to parse.</param>
    /// <returns>A Version object for comparison, or 0.0.0 if parsing fails.</returns>
    public static Version ParseVersion(string versionString)
    {
        // Remove common prefixes
        var cleaned = versionString.TrimStart('v', 'V');

        // Handle prerelease versions by taking only the numeric part
        var dashIndex = cleaned.IndexOf('-');
        if (dashIndex > 0)
            cleaned = cleaned[..dashIndex];

        // Try to parse as Version, fall back to 0.0.0 if invalid
        return Version.TryParse(cleaned, out var version) ? version : new Version(0, 0, 0);
    }
}
