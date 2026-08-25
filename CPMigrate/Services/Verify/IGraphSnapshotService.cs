using CPMigrate.Models;

namespace CPMigrate.Services.Verify;

/// <summary>
/// Restores a solution and captures what every project actually resolved to.
/// </summary>
public interface IGraphSnapshotService
{
    /// <summary>
    /// Clears each project's previous <c>obj/project.assets.json</c>, restores
    /// <paramref name="restoreTargetPath"/>, then reads the resolved graph of each project. A file
    /// readable after the restore therefore provably came from that restore, not from an earlier
    /// build; absence after a succeeded restore is recorded as unreadable and fails closed.
    /// </summary>
    /// <param name="restoreTargetPath">The solution or project to restore.</param>
    /// <param name="projectPaths">Every project whose graph forms part of the snapshot.</param>
    /// <param name="basePath">
    /// The scan root, used to name each project by its path relative to it. Projects are identified
    /// this way rather than by absolute path so the receipt is byte-identical on every machine —
    /// the same reason <c>analysisIssues[].affectedProjects</c> moved to relative paths in output
    /// schema 1.3.0.
    /// </param>
    Task<GraphSnapshotResult> CaptureAsync(
        string restoreTargetPath,
        IReadOnlyList<string> projectPaths,
        string? basePath
    );
}
