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
}
