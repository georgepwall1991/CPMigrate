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
/// <param name="BasePath">
/// Directory the scan was rooted at. Findings identify projects by their path relative to this, so
/// that identity is unambiguous (two projects can share a file name) and portable (a machine-specific
/// absolute path would make a committed baseline useless on any other machine).
/// </param>
public record ProjectPackageInfo(
    IReadOnlyList<PackageReference> References,
    IReadOnlyList<VulnerabilityInfo>? Vulnerabilities = null,
    IReadOnlyList<OutdatedPackageInfo>? OutdatedPackages = null,
    IReadOnlyList<DeprecatedPackageInfo>? DeprecatedPackages = null,
    string? BasePath = null,
    IReadOnlyList<string>? ScannedProjects = null
)
{
    /// <summary>
    /// Every project the scan covered, whether or not it contributed a package reference.
    ///
    /// Deriving this from <see cref="References"/> silently loses projects: the fallback scanner
    /// skips <c>PackageReference</c> items with no version, so a *correctly* centralized project can
    /// contribute nothing at all. Any analyzer that needs to look at projects rather than at
    /// packages has to start here.
    /// </summary>
    public IReadOnlyList<string> GetProjectsScanned()
    {
        return ScannedProjects
            ?? References
                .Select(reference => reference.ProjectPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>
    /// The stable identifier for a project: its path relative to the scan root, with forward
    /// slashes so the value is identical on every platform.
    ///
    /// Falls back to the file name when the path cannot be made relative — a project outside the
    /// scan root, or a scan with no root recorded. That reintroduces the ambiguity this method
    /// exists to remove, but a name is still more useful to a reader than an absolute path, and it
    /// keeps a committed baseline from embedding one.
    /// </summary>
    /// <param name="projectPath">Full path to the project file.</param>
    public string ProjectId(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(BasePath))
        {
            return Path.GetFileName(projectPath);
        }

        var relative = Path.GetRelativePath(BasePath, Path.GetFullPath(projectPath));
        if (EscapesRoot(relative))
        {
            return Path.GetFileName(projectPath);
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
    }

    /// <summary>
    /// True when a relative path leaves the directory it was computed against.
    ///
    /// Tests the first <em>segment</em> rather than the string prefix: a directory can legitimately
    /// be named <c>..generated</c>, and treating that as an escape would discard the directory and
    /// recreate exactly the file-name collisions this identifier exists to prevent.
    /// </summary>
    /// <param name="relativePath">A path produced by <see cref="Path.GetRelativePath"/>.</param>
    public static bool EscapesRoot(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            return Path.IsPathRooted(relativePath);
        }

        var firstSegment = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/'],
            2
        )[0];

        return firstSegment == "..";
    }

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
