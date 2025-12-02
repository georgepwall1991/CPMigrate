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
        // Parse versions using NuGetVersion
        var nuGetVersions = versions
            .Select(v =>
            {
                if (NuGetVersion.TryParse(v, out var nuVer))
                {
                    return nuVer;
                }
                // Log warning for invalid version format
                _consoleService?.Warning($"Invalid version format '{v}' - treating as 0.0.0");
                // Fallback for invalid versions, we create a "0.0.0" version to sort it lowest
                return new NuGetVersion(0, 0, 0);
            })
            .OrderBy(v => v)
            .ToList();

        if (!nuGetVersions.Any())
        {
            // Should not happen given caller context, but return something safe
            return "0.0.0"; 
        }

        var selectedVersion = strategy switch
        {
            ConflictStrategy.Highest => nuGetVersions.Last(),
            ConflictStrategy.Lowest => nuGetVersions.First(),
            ConflictStrategy.Fail => nuGetVersions.Last(), // Should be handled by caller logic
            _ => nuGetVersions.Last()
        };

        return selectedVersion.ToNormalizedString();
    }
}