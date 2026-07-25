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

    /// <summary>
    /// Number of transitive dependencies found during scanning.
    /// </summary>
    public int TransitivePackagesFound { get; init; }

    /// <summary>
    /// Number of transitive dependencies that were pinned/updated.
    /// </summary>
    public int TransitivePackagesUpdated { get; init; }

    /// <summary>
    /// Number of accepted updates that did not survive verification. Under <c>--bisect</c> these are the
    /// culprits the search isolated; without it, a failure holds back the whole set.
    /// </summary>
    public int PackagesHeldBack { get; init; }

    /// <summary>
    /// Number of restore+test cycles executed. Zero when verification never ran (dry-run, no updates).
    /// </summary>
    public int VerificationRuns { get; init; }

    /// <summary>
    /// Whether bisection stopped early because it exhausted <c>--bisect-budget</c>.
    /// </summary>
    public bool BisectBudgetExhausted { get; init; }
}

/// <summary>
/// Represents a single package update entry.
/// </summary>
/// <param name="PackageName">The NuGet package ID.</param>
/// <param name="CurrentVersion">The version currently in Directory.Packages.props.</param>
/// <param name="LatestVersion">The latest available version from NuGet.</param>
/// <param name="IsMajorUpdate">Whether this update crosses a major version boundary.</param>
/// <param name="Accepted">Whether the user accepted this update in the wizard.</param>
/// <param name="IsTransitive">Whether this is a transitive (indirect) dependency.</param>
/// <param name="HeldBack">
/// Whether this accepted update was excluded because keeping it broke verification. Under <c>--bisect</c>
/// this marks the isolated culprits; without it, every accepted update is marked when the run rolls back.
/// </param>
public record PackageUpdateEntry(
    string PackageName,
    string CurrentVersion,
    string LatestVersion,
    bool IsMajorUpdate,
    bool Accepted,
    bool IsTransitive = false,
    bool HeldBack = false);
