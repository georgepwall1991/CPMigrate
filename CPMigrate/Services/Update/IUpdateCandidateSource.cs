using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// Discovers which packages have a newer version available, using the same feeds restore uses.
/// </summary>
public interface IUpdateCandidateSource
{
    /// <summary>
    /// Finds update candidates for the packages pinned in <paramref name="currentVersions"/>.
    /// </summary>
    /// <param name="projectPaths">Project files to query, in discovery order.</param>
    /// <param name="currentVersions">Pins from <c>Directory.Packages.props</c>, keyed case-insensitively.</param>
    /// <param name="includeTransitive">Whether to also propose pins for outdated transitive dependencies.</param>
    /// <param name="includePrerelease">Whether pre-release versions count as "latest".</param>
    /// <param name="maxParallelism">Process-wide scan ceiling, as <c>--max-parallelism</c> resolves it.</param>
    Task<UpdateCandidateScan> FindAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyDictionary<string, HashSet<string>> currentVersions,
        bool includeTransitive,
        bool includePrerelease,
        int maxParallelism);
}

/// <summary>
/// The result of one candidate discovery pass.
/// </summary>
/// <param name="Updates">
/// One entry per package id. <see cref="PackageUpdateEntry.CurrentVersion"/> for a direct pin is
/// the version in the props file, not the resolved version; transitive entries use the highest
/// resolved version seen. <see cref="PackageUpdateEntry.Accepted"/> is <c>true</c> for non-major
/// updates so the major-version wizard can leave minors auto-accepted.
/// </param>
/// <param name="UnscannedProjects">
/// Project file names whose outdated scan did not finish. A non-empty list means the run cannot
/// prove it asked every feed-visible project, and must not write.
/// </param>
/// <param name="TransitivePackagesFound">
/// Unique transitive package ids discovered (including those already pinned as direct), matching
/// the count <c>--update-packages --transitive</c> has always published in JSON. Zero when
/// transitive scanning was not requested or every transitive scan failed.
/// </param>
/// <param name="TransitiveScanFailed">
/// True when transitive scanning was requested and no project produced a successful transitive
/// inventory. Direct candidates are still valid; the caller warns and continues without pins.
/// </param>
public sealed record UpdateCandidateScan(
    IReadOnlyList<PackageUpdateEntry> Updates,
    IReadOnlyList<string> UnscannedProjects,
    int TransitivePackagesFound,
    bool TransitiveScanFailed = false);
