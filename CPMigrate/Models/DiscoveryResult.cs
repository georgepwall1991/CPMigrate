namespace CPMigrate.Models;

/// <summary>
/// What workspace discovery found — including the projects it could not return.
/// </summary>
/// <param name="BasePath">Directory the solution lives in; empty when discovery failed.</param>
/// <param name="ProjectPaths">Projects that exist on disk, ready to scan.</param>
/// <param name="MissingProjects">
/// Projects the solution names but the filesystem does not have. They are reported separately
/// rather than dropped: a consumer that only sees <paramref name="ProjectPaths"/> would report a
/// complete scan over a workspace that is not, which is exactly the lie <c>failedScans</c> exists
/// to prevent.
/// </param>
public sealed record DiscoveryResult(
    string BasePath,
    List<string> ProjectPaths,
    List<string> MissingProjects
);
