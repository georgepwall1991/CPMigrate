using NuGet.Versioning;

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
)
{
    /// <summary>
    /// Whether this is a standalone <c>PackageReference Update</c> with no version metadata. It is retained
    /// for declaration-based rules such as casing checks, but it does not declare a version and must not
    /// decide whether a resolved version is conditionally pinned.
    /// </summary>
    public bool IsMetadataOnlyUpdate { get; init; }

    /// <summary>
    /// Whether the declaration explicitly carries <c>Version</c>, including an empty value. An empty
    /// conditional assignment can expose a central version in that branch and must remain distinguishable
    /// from an Update that does not mention the metadata at all.
    /// </summary>
    public bool HasVersionMetadata { get; init; }

    /// <summary>
    /// Whether this declaration's conditionality came from a conditional <c>Update</c> statement rather
    /// than from the item being conditionally included. A later unconditional Update overrides the former
    /// statement, but it does not remove conditionality from an item that was conditionally included.
    /// </summary>
    public bool IsConditionalUpdate { get; init; }

    /// <summary>
    /// Whether the declaration explicitly carries <c>VersionOverride</c>, including an empty value.
    /// An empty conditional assignment clears inherited override metadata and must remain distinguishable
    /// from an Update that does not mention the metadata at all.
    /// </summary>
    public bool HasVersionOverrideMetadata { get; init; }

    /// <summary>
    /// The ordered condition expressions that scope a conditional Update. Used to distinguish sequential
    /// Updates in one conditional branch from Updates in different branches.
    /// </summary>
    public string? ConditionalScope { get; init; }
}

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
                !reference.IsMetadataOnlyUpdate
                &&
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

        // Per *effective version*, not per package. A project can pin a package unconditionally and override
        // it for one framework, and the two questions have opposite answers: asking about the pair either
        // hid the unconditional pin from comparison, or left the conditional override in it — where the
        // Highest strategy would pick the override and rewrite everyone else to a version meant for one
        // framework. VersionOverride is the effective version when present, even though Version stays empty
        // because the project is centrally managed.
        var matching = declarations
            .Where(reference =>
                string.Equals(
                    reference.VersionOverride ?? reference.Version,
                    version,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        // An unconditional declaration that names the resolved version is authoritative even when another
        // conditional declaration is versionless or non-literal. The conditional form cannot make the
        // concrete unconditional pin disappear from a version comparison.
        if (matching.Any(reference => !reference.IsConditional))
        {
            return false;
        }

        // An explicit conditional Version="" clears the inline value and exposes the central pin only
        // in that branch. The resolved graph carries that concrete central version, so preserve the
        // conditional protection even though the declaration's normalized Version is empty.
        var hasConditionalVersionClear = declarations
            .Select((reference, index) => (reference, index))
            .Any(item =>
                item.reference.IsConditionalUpdate
                && item.reference.HasVersionMetadata
                && string.IsNullOrWhiteSpace(item.reference.Version)
                && item.reference.VersionOverride is null
                && declarations
                    .Take(item.index)
                    .Any(previous =>
                        previous.HasVersionMetadata
                        && !string.IsNullOrWhiteSpace(previous.Version)
                        && ConditionalScopesMayOverlap(
                            previous.ConditionalScope,
                            item.reference.ConditionalScope
                        )
                    )
            );
        if (hasConditionalVersionClear)
        {
            return true;
        }

        // A conditional non-literal pin is deliberately treated as protected. The resolved graph contains
        // the value selected for one evaluation, but it cannot tell us whether another target gets a
        // different value from a floating range, a NuGet range, or an MSBuild property. A blank version is
        // not non-literal here: it is a central/versionless declaration, which the fallback below handles.
        if (
            declarations.Any(reference =>
                reference.IsConditional
                && IsNonLiteralVersion(reference.VersionOverride ?? reference.Version)
            )
        )
        {
            return true;
        }

        // No declaration names this version — it came from a central pin, so the package-level answer is
        // the only one available.
        return matching.Count > 0
            ? matching.TrueForAll(reference => reference.IsConditional)
            : declarations.TrueForAll(reference => reference.IsConditional);
    }

    private static bool IsNonLiteralVersion(string version)
    {
        return !string.IsNullOrWhiteSpace(version)
            && (
                version.Contains("$(", StringComparison.Ordinal)
                || !NuGetVersion.TryParse(version, out _)
            );
    }

    private static bool ConditionalScopesMayOverlap(string? leftScope, string? rightScope)
    {
        if (leftScope is null || rightScope is null)
        {
            return true;
        }

        var leftConditions = leftScope.Split(" -> ", StringSplitOptions.None);
        var rightConditions = rightScope.Split(" -> ", StringSplitOptions.None);
        return !leftConditions.Any(leftCondition =>
            rightConditions.Any(rightCondition =>
                AreMutuallyExclusiveConditions(leftCondition, rightCondition)
            )
        );
    }

    private static bool AreMutuallyExclusiveConditions(string left, string right)
    {
        var leftPart = SplitConditionalScopePart(left);
        var rightPart = SplitConditionalScopePart(right);

        if (
            leftPart.BranchPath is not null
            && rightPart.BranchPath is not null
            && string.Equals(leftPart.Condition, "<Otherwise>", StringComparison.Ordinal)
                != string.Equals(rightPart.Condition, "<Otherwise>", StringComparison.Ordinal)
        )
        {
            return SameChoose(leftPart.BranchPath, rightPart.BranchPath);
        }

        if (
            leftPart.BranchPath is not null
            && rightPart.BranchPath is not null
            && SameChoose(leftPart.BranchPath, rightPart.BranchPath)
        )
        {
            return !SameChooseBranch(leftPart.BranchPath, rightPart.BranchPath);
        }

        return TryGetSimpleEquality(leftPart.Condition, out var leftProperty, out var leftValue)
            && TryGetSimpleEquality(rightPart.Condition, out var rightProperty, out var rightValue)
            && string.Equals(leftProperty, rightProperty, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Condition, string? BranchPath) SplitConditionalScopePart(string part)
    {
        var separator = part.LastIndexOf('@');
        if (separator < 0 || separator == part.Length - 1)
        {
            return (part, null);
        }

        var branchPath = part[(separator + 1)..];
        var pathSeparator = branchPath.IndexOf('|');
        var choosePath = pathSeparator < 0 ? branchPath : branchPath[..pathSeparator];
        return choosePath.Length > 0
            && choosePath.All(character => char.IsDigit(character) || character == '.')
            ? (part[..separator], branchPath)
            : (part, null);
    }

    private static bool SameChoose(string leftBranchPath, string rightBranchPath)
    {
        var leftPath = BranchLocation(leftBranchPath);
        var rightPath = BranchLocation(rightBranchPath);
        var leftSeparator = leftPath.LastIndexOf('.');
        var rightSeparator = rightPath.LastIndexOf('.');
        return leftSeparator >= 0
            && rightSeparator >= 0
            && string.Equals(
                leftPath[..leftSeparator],
                rightPath[..rightSeparator],
                StringComparison.Ordinal
            );
    }

    private static bool SameChooseBranch(string leftBranchPath, string rightBranchPath)
    {
        return string.Equals(
            BranchLocation(leftBranchPath),
            BranchLocation(rightBranchPath),
            StringComparison.Ordinal
        );
    }

    private static string BranchLocation(string branchPath)
    {
        var guardSeparator = branchPath.IndexOf('|');
        return guardSeparator < 0 ? branchPath : branchPath[..guardSeparator];
    }

    private static bool TryGetSimpleEquality(
        string condition,
        out string property,
        out string value
    )
    {
        property = string.Empty;
        value = string.Empty;
        if (
            condition.Contains("&&", StringComparison.Ordinal)
            || condition.Contains("||", StringComparison.Ordinal)
            || condition.Contains(" and ", StringComparison.OrdinalIgnoreCase)
            || condition.Contains(" or ", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        var equality = condition.IndexOf("==", StringComparison.Ordinal);
        if (equality < 0)
        {
            return false;
        }

        property = condition[..equality].Trim().Trim('"', '\'');
        value = condition[(equality + 2)..].Trim().Trim('"', '\'');
        return property.StartsWith("$(", StringComparison.Ordinal)
            && property.EndsWith(")", StringComparison.Ordinal)
            && value.Length > 0
            && !value.Contains("$(", StringComparison.Ordinal);
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
