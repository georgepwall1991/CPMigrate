namespace CPMigrate.Models;

/// <summary>
/// Result of the package update operation.
/// </summary>
public class PackageUpdateResult
{
    /// <summary>
    /// Exit code for the operation.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Number of packages checked for updates.
    /// </summary>
    public int PackagesChecked { get; init; }

    /// <summary>
    /// Number of packages that were updated.
    /// </summary>
    public int PackagesUpdated { get; init; }

    /// <summary>
    /// Number of packages skipped (user chose to skip or already up to date).
    /// </summary>
    public int PackagesSkipped { get; init; }

    /// <summary>
    /// Whether tests passed after updating.
    /// </summary>
    public bool TestsPassed { get; init; }

    /// <summary>
    /// Whether the props file was rolled back due to test failure.
    /// </summary>
    public bool WasRolledBack { get; init; }

    /// <summary>
    /// Individual package update entries.
    /// </summary>
    public List<PackageUpdateEntry> Updates { get; init; } = [];
}

/// <summary>
/// Represents a single package update entry.
/// </summary>
/// <param name="PackageName">The NuGet package ID.</param>
/// <param name="CurrentVersion">The version currently in Directory.Packages.props.</param>
/// <param name="LatestVersion">The latest available version from NuGet.</param>
/// <param name="IsMajorUpdate">Whether this update crosses a major version boundary.</param>
/// <param name="Accepted">Whether the user accepted this update in the wizard.</param>
public record PackageUpdateEntry(
    string PackageName,
    string CurrentVersion,
    string LatestVersion,
    bool IsMajorUpdate,
    bool Accepted);
