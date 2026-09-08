using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Interface for querying NuGet for latest package versions.
/// </summary>
public interface INuGetVersionLookupService : IDisposable
{
    /// <summary>
    /// Packages whose version could not be determined this run, after retries.
    ///
    /// A null result from the lookup methods means "no newer version" *or* "could not ask", and those
    /// are very different answers: the first is a clean result, the second means the run silently
    /// skipped a package. Callers report this so an incomplete update is visible.
    /// </summary>
    /// <returns>Package IDs whose version could not be determined.</returns>
    IReadOnlyCollection<string> GetFailedLookups();

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

    /// <summary>
    /// Gets every published version of a package, newest first.
    ///
    /// Distinct from <see cref="GetLatestVersionAsync"/> because remediation asks a different
    /// question: not "what is newest" but "what is the lowest version that clears this advisory".
    /// Answering that needs the whole list, and picking the newest instead would turn a one-patch
    /// bump into an unrelated major upgrade.
    /// </summary>
    /// <param name="packageId">The NuGet package ID.</param>
    /// <returns>
    /// Every published version sorted descending, including pre-release; null if the package was not
    /// found or the lookup failed. Callers distinguish the two through <see cref="GetFailedLookups"/>.
    /// </returns>
    Task<IReadOnlyList<NuGetVersion>?> GetAllVersionsAsync(string packageId);
}
