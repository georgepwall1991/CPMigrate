using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Interface for analyzing the full dependency graph using project.assets.json.
/// </summary>
public interface IDependencyGraphService
{
    /// <summary>
    /// Identifies redundant direct references in a project.
    /// A reference is redundant if it's already provided transitively by another top-level package at the same or higher version.
    /// </summary>
    List<string> IdentifyRedundantDirectReferences(string projectFilePath);

    /// <summary>
    /// Reads what restore actually resolved for a project — every package, at the version that will be
    /// built against, per target framework.
    /// </summary>
    /// <returns>
    /// The graph, or <c>null</c> when the assets file is absent, unparseable, or missing the sections
    /// this reads. Null is deliberately not an empty graph: an unrestored project has to stay
    /// distinguishable from one that restored to nothing, because a caller comparing two snapshots
    /// would otherwise read the first as "every package removed" — or, having seen it in neither
    /// snapshot, as no change at all.
    /// </returns>
    ProjectResolvedGraph? TryReadResolvedGraph(string projectFilePath);

    /// <summary>
    /// Removes a project's <c>obj/project.assets.json</c> so that the next restore has to write it
    /// afresh, making "a readable assets file after this restore" mean "this restore wrote it".
    /// </summary>
    /// <remarks>
    /// A capture that restores and then reads can be fooled by its own subject. When a project
    /// redirects its intermediate output, plain <c>dotnet restore</c> writes the real graph
    /// elsewhere, while an older <c>obj/project.assets.json</c> — written before the redirect, by
    /// this very project's last local build — still sits at the default path saying exactly what a
    /// read wants to hear: right project, parseable graph, plausible versions. Two snapshots would
    /// then compare that stale file against itself and report an unchanged resolved graph over a
    /// migration whose effect was never observed.
    /// </remarks>
    /// <returns>
    /// True when no assets file remains at the default path — absent, or removed here. False only
    /// when one is present and could not be removed: the caller must then refuse to trust any later
    /// read of this project rather than guess whether it is looking at this run's output.
    /// </returns>
    bool TryClearResolvedGraph(string projectFilePath);
}
