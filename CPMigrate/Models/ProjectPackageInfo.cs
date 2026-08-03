namespace CPMigrate.Models;

/// <summary>
/// Represents a package reference found in a specific project.
/// </summary>
/// <param name="PackageName">The name of the package as specified in the PackageReference.</param>
/// <param name="Version">The version string of the package.</param>
/// <param name="ProjectPath">Full path to the project file containing this reference.</param>
/// <param name="ProjectName">The file name of the project (e.g., "MyProject.csproj").</param>
/// <param name="IsConditional">
/// Whether the declaration, or the item group holding it, carries a <c>Condition</c>. Two conditional
/// declarations of one package are how multi-targeting is expressed, not a mistake, so a rule about
/// duplicates has to be able to tell them apart from two unconditional ones. Only ever set on references
/// read from the project file; the resolved graph has no conditions left in it.
/// </param>
/// <param name="VersionOverride">
/// The <c>VersionOverride</c> metadata, when the declaration carries one. Under central package
/// management this is the sanctioned way for a project to step outside the central pin, so it is the
/// version actually in force — and it can float exactly as a <c>Version</c> can. Kept separate
/// rather than folded into <see cref="Version"/>, because the rules that ask "does this project pin
/// inline?" and "what version does this project get?" want opposite answers about it. Only ever set
/// on references read from the project file.
/// </param>
public record PackageReference(
    string PackageName,
    string Version,
    string ProjectPath,
    string ProjectName,
    bool IsTransitive = false,
    bool IsConditional = false,
    string? VersionOverride = null
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
    IReadOnlyList<string>? ScannedProjects = null,
    IReadOnlyList<PackageReference>? DeclaredReferences = null
)
{
    /// <summary>
    /// Whether a project declares this package behind a <c>Condition</c>.
    ///
    /// A multi-targeted project pinning a different version per framework is the ordinary way to say "the
    /// newer one does not support net8.0". The resolved reference list has no conditions left in it, so a
    /// rule comparing versions across projects would read those two pins as an inconsistency and — being
    /// fixable — have them unified to the highest, silently breaking the target that needed the older one.
    /// </summary>
    public bool IsConditionallyDeclared(string projectPath, string packageName, string version)
    {
        if (DeclaredReferences is null)
        {
            return false;
        }

        var declarations = DeclaredReferences
            .Where(reference =>
                string.Equals(
                    reference.ProjectPath,
                    projectPath,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    reference.PackageName,
                    packageName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        if (declarations.Count == 0)
        {
            return false;
        }

        // Per *version*, not per package. A project can pin a package unconditionally and override it for
        // one framework, and the two questions have opposite answers: asking about the pair either hid the
        // unconditional pin from comparison, or left the conditional override in it — where the Highest
        // strategy would pick the override and rewrite everyone else to a version meant for one framework.
        var matching = declarations
            .Where(reference =>
                string.Equals(reference.Version, version, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        // No declaration names this version — it came from a central pin, so the package-level answer is
        // the only one available.
        return matching.Count > 0
            ? matching.TrueForAll(reference => reference.IsConditional)
            : declarations.TrueForAll(reference => reference.IsConditional);
    }

    /// <summary>
    /// Turns an <c>AffectedProjects</c> entry back into a path on disk.
    ///
    /// Those entries have held a project *id* — the path relative to the scan root — since 3.10.0, when
    /// file names were dropped as identifiers because two projects can share one. A fixer matching them
    /// against <c>ProjectName</c> therefore never matches, and a fixer that finds no project reports "no
    /// fix needed" rather than failing: the finding stays, the run exits reporting nothing to do, and the
    /// two statements are individually true and jointly misleading.
    /// </summary>
    public string? ResolveProjectPath(string projectId)
    {
        return GetProjectsScanned()
            .FirstOrDefault(path =>
                string.Equals(ProjectId(path), projectId, StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    /// The references as the project files declare them, before NuGet resolves anything.
    ///
    /// <see cref="References"/> comes from <c>dotnet package list</c>, which reports the *resolved* graph
    /// — and resolution collapses two <c>PackageReference</c> items with the same Include into one. Any
    /// rule about what the project file says, rather than about what restore produced, has to read this
    /// instead. Reading the resolved list is why <c>RedundantReference</c> could not fire: by the time
    /// the duplicate reached the analyzer there was only ever one of it.
    ///
    /// Falls back to the resolved list only when the project files could not be read *at all* — a null,
    /// not an empty list. A successful scan that found no declarations is an answer: with --transitive on a
    /// multi-targeted project, the resolved parser lists the same transitive package once per framework, so
    /// treating "none declared" as "no data" would have those read as duplicate declarations of a package
    /// the project never declares.
    /// </summary>
    public IReadOnlyList<PackageReference> GetDeclaredReferences()
    {
        return DeclaredReferences ?? References;
    }

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
    public string ProjectId(string projectPath) => ProjectId(BasePath, projectPath);

    /// <summary>
    /// The same identifier, for callers that hold a scan root but not a
    /// <see cref="ProjectPackageInfo"/> — the resolved-graph verification, which reports on projects
    /// rather than on findings.
    /// </summary>
    /// <remarks>
    /// One implementation on purpose. A second way of naming a project would drift from this one, and
    /// the two would disagree about the same project in the same payload.
    /// </remarks>
    public static string ProjectId(string? basePath, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(basePath))
        {
            return Path.GetFileName(projectPath);
        }

        var relative = Path.GetRelativePath(basePath, Path.GetFullPath(projectPath));
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
