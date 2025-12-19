namespace CPMigrate.Models;

/// <summary>
/// Represents a package reference found in a specific project.
/// </summary>
/// <param name="PackageName">The name of the package as specified in the PackageReference.</param>
/// <param name="Version">The version string of the package.</param>
/// <param name="ProjectPath">Full path to the project file containing this reference.</param>
/// <param name="ProjectName">The file name of the project (e.g., "MyProject.csproj").</param>
public record PackageReference(
    string PackageName,
    string Version,
    string ProjectPath,
    string ProjectName,
    bool IsTransitive = false
);

/// <summary>
/// Contains all package references discovered from a set of projects.
/// </summary>
/// <param name="References">All package references found across all projects.</param>
public record ProjectPackageInfo(
    IReadOnlyList<PackageReference> References
)
{
    /// <summary>
    /// Gets the total number of package references.
    /// </summary>
    public int TotalReferences => References.Count;

    /// <summary>
    /// Gets the distinct project count.
    /// </summary>
    public int ProjectCount => References.Select(r => r.ProjectPath).Distinct().Count();
}
