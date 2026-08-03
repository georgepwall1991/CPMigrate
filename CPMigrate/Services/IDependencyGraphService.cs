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
}
