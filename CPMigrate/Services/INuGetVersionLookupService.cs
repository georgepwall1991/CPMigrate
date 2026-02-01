using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Interface for querying NuGet for latest package versions.
/// </summary>
public interface INuGetVersionLookupService : IDisposable
{
    /// <summary>
    /// Gets the latest version of a NuGet package.
    /// </summary>
    /// <param name="packageId">The NuGet package ID.</param>
    /// <param name="includePrerelease">Whether to include pre-release versions.</param>
    /// <returns>The latest version, or null if the package was not found or lookup failed.</returns>
    Task<NuGetVersion?> GetLatestVersionAsync(string packageId, bool includePrerelease = false);

    /// <summary>
    /// Gets the latest version within a specific major version range.
    /// </summary>
    /// <param name="packageId">The NuGet package ID.</param>
    /// <param name="majorVersion">The major version to constrain to.</param>
    /// <param name="includePrerelease">Whether to include pre-release versions.</param>
    /// <returns>The latest version within the major version, or null if not found.</returns>
    Task<NuGetVersion?> GetLatestVersionInMajorAsync(string packageId, int majorVersion, bool includePrerelease = false);
}
