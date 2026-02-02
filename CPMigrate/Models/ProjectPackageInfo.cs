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
/// Represents an outdated package discovered by dotnet package list --outdated.
/// </summary>
public record OutdatedPackageInfo(
    string PackageName,
    string ResolvedVersion,
    string LatestVersion,
    string ProjectPath,
    string ProjectName,
    bool IsTransitive = false
);

/// <summary>
/// Represents a deprecated package discovered by dotnet package list --deprecated.
/// </summary>
public record DeprecatedPackageInfo(
    string PackageName,
    string ResolvedVersion,
    string ProjectPath,
    string ProjectName,
    IReadOnlyList<string> Reasons,
    string? AlternativePackage = null,
    string? AlternativeVersionRange = null,
    bool IsTransitive = false
);

/// <summary>
/// Contains all package references discovered from a set of projects.
/// </summary>
/// <param name="References">All package references found across all projects.</param>
/// <param name="Vulnerabilities">Optional vulnerability findings from CLI scan.</param>
/// <param name="OutdatedPackages">Optional outdated package findings from CLI scan.</param>
/// <param name="DeprecatedPackages">Optional deprecated package findings from CLI scan.</param>
public record ProjectPackageInfo(
    IReadOnlyList<PackageReference> References,
    IReadOnlyList<VulnerabilityInfo>? Vulnerabilities = null,
    IReadOnlyList<OutdatedPackageInfo>? OutdatedPackages = null,
    IReadOnlyList<DeprecatedPackageInfo>? DeprecatedPackages = null
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

    /// <summary>
    /// Gets the total number of vulnerabilities found.
    /// </summary>
    public int VulnerabilityCount => Vulnerabilities?.Count ?? 0;

    /// <summary>
    /// Gets the total number of outdated package findings.
    /// </summary>
    public int OutdatedCount => OutdatedPackages?.Count ?? 0;

    /// <summary>
    /// Gets the total number of deprecated package findings.
    /// </summary>
    public int DeprecatedCount => DeprecatedPackages?.Count ?? 0;
}
