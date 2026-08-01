using System.Xml;
using System.Xml.Linq;
using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Detects drift between a solution and its <c>Directory.Packages.props</c>.
///
/// Migrating to central package management is a one-off event; staying migrated is not. Someone adds
/// a package the way they always have — <c>&lt;PackageReference Include="X" Version="1.0.0" /&gt;</c> —
/// and the solution is quietly half-centralized again. NuGet does not complain about an inline
/// version (it overrides the central one), so nothing surfaces until two projects disagree and
/// something breaks at runtime. A missing <c>PackageVersion</c> is louder, failing restore, but by
/// then it is already committed.
///
/// Gated on data rather than a flag, like the other analyzers: it reports nothing unless the solution
/// actually has a <c>Directory.Packages.props</c>, so a pre-migration repository sees no findings from
/// it at all.
/// </summary>
public class CpmDriftAnalyzer : IAnalyzer
{
    /// <summary>The conventional central props file name.</summary>
    public const string PropsFileName = "Directory.Packages.props";

    private const string PackageVersionItem = "PackageVersion";
    private const string GlobalPackageReferenceItem = "GlobalPackageReference";
    private const string PackageReferenceItem = "PackageReference";
    private const string EnablementProperty = "ManagePackageVersionsCentrally";

    /// <summary>Unit separator: it cannot occur in a path, so composite keys stay unambiguous.</summary>
    private const string KeySeparator = "\u001F";

    /// <inheritdoc />
    public string Name => "Central Package Management Drift";

    /// <inheritdoc />
    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);

        var issues = new List<AnalysisIssue>();

        // Grouped by the props file that actually governs each project, because MSBuild resolves
        // Directory.Packages.props from the project's own directory. Judging every project against
        // one set is wrong in both directions: a project under a nested props file gets measured
        // against pins it never sees — MissingPackageVersion, a High finding, on references that
        // restore perfectly well — and a pin is called orphaned because the projects using it were
        // reading a different file.
        var governed = GroupProjectsByGoverningProps(packageInfo, issues);

        // Sorted so a report is identical run to run, whatever order discovery happened to produce.
        foreach (
            var (propsPath, context) in governed.OrderBy(entry => entry.Key, StringComparer.Ordinal)
        )
        {
            // A GlobalPackageReference applies to every project by definition, so it is never
            // orphaned — seeding it here keeps the orphan check from reporting all of them.
            var referenced = new HashSet<string>(
                context.Central.Where(entry => entry.Value.IsGlobal).Select(entry => entry.Key),
                StringComparer.OrdinalIgnoreCase
            );

            foreach (var projectPath in context.Projects)
            {
                InspectProject(
                    issues,
                    packageInfo,
                    projectPath,
                    context.Central,
                    referenced,
                    context.ImportsResolved
                );
            }

            // Both remaining rules compare against the full central set. If an import could not be
            // followed, that set is incomplete, and every conclusion drawn from it would be a guess.
            // Asked from each governed project's directory, and satisfied by any of them.
            // Transitive pinning makes a pin that nothing references deliberate, so treating it as
            // on wherever it might be errs towards not accusing a project that is fine.
            var transitivePinning =
                context.PropertyRoots.Count == 0
                    ? IsTransitivePinningEnabled(context.Props, propsPath, packageInfo.BasePath)
                    : context.PropertyRoots.Any(root =>
                        IsTransitivePinningEnabled(context.Props, propsPath, root)
                    );

            if (context.ImportsResolved && !transitivePinning)
            {
                AddOrphanedVersionIssues(
                    issues,
                    context.Central,
                    referenced,
                    DescribePropsPath(propsPath, packageInfo)
                );
            }
        }

        return new AnalyzerResult(Name, issues);
    }

    /// <summary>
    /// The props file governing each scanned project, and the projects it governs.
    ///
    /// <para>
    /// Unusable files — unparseable, or with central management switched off — are reported once
    /// each and then excluded. Once per <em>file</em>, not once per project beneath it: the
    /// misconfiguration is a property of the file, and repeating it turns one problem into a wall.
    /// </para>
    /// </summary>
    private static Dictionary<string, CentralContext> GroupProjectsByGoverningProps(
        ProjectPackageInfo packageInfo,
        List<AnalysisIssue> issues
    )
    {
        var usable = new Dictionary<string, CentralContext>(StringComparer.OrdinalIgnoreCase);
        // Keyed by props file *and* the directory the properties were resolved from, because two
        // projects can share a props file while a nearer Directory.Build.props enables central
        // management for one and not the other. Caching by props file alone let whichever project
        // came first decide for both.
        var enablement = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = packageInfo.GetProjectsScanned();

        // With no projects at all there is still a solution-level props file worth checking, so a
        // misconfigured one is reported rather than passing unexamined.
        var directories =
            projects.Count > 0
                ? projects.Select(project =>
                    (
                        Project: (string?)project,
                        Directory: Path.GetDirectoryName(Path.GetFullPath(project))
                    )
                )
                : [(Project: (string?)null, Directory: packageInfo.BasePath)];

        foreach (var (project, directory) in directories)
        {
            var propsPath = ResolvePropsPath(directory);
            if (propsPath is null)
            {
                // Not centrally managed. Reporting "no props file" would fire on every
                // pre-migration repository.
                continue;
            }

            // Resolved from the governed project's own directory, not the scan root: a nested
            // project can rely on a nearer Directory.Build.props for ManagePackageVersionsCentrally,
            // and reading the wrong one reports a valid project as CpmNotEnabled — a High finding.
            var propertyRoot = directory ?? packageInfo.BasePath;
            var enablementKey = $"{propsPath}{KeySeparator}{propertyRoot}";

            if (!enablement.TryGetValue(enablementKey, out var enabled))
            {
                enabled = IsUsable(propsPath, propertyRoot, packageInfo, issues, reported);
                enablement[enablementKey] = enabled;
            }

            if (!enabled)
            {
                continue;
            }

            if (!usable.TryGetValue(propsPath, out var context))
            {
                // Created only once a project has proved the file usable, so a props file every
                // project turned out to be exempt from never reaches the orphan check.
                var props = ReadProps(propsPath)!;
                var (central, importsResolved) = ReadCentralVersions(props, propsPath);
                context = new CentralContext(props, central, importsResolved);
                usable[propsPath] = context;
            }

            if (project is not null)
            {
                context.Projects.Add(project);

                // Kept so the orphan check can ask each governed project whether transitive pinning
                // is on for it, rather than asking once from the scan root.
                if (propertyRoot is not null)
                {
                    context.PropertyRoots.Add(propertyRoot);
                }
            }
        }

        return usable;
    }

    /// <summary>
    /// Whether one props file can be used for a project resolving properties from a given
    /// directory, reporting why not rather than proceeding on a guess.
    ///
    /// <para>
    /// A reason is reported once per file and enablement value, not once per project beneath it:
    /// the misconfiguration is a property of the file, and repeating it turns one problem into a
    /// wall.
    /// </para>
    /// </summary>
    private static bool IsUsable(
        string propsPath,
        string? propertyRoot,
        ProjectPackageInfo packageInfo,
        List<AnalysisIssue> issues,
        HashSet<string> reported
    )
    {
        var propsFile = DescribePropsPath(propsPath, packageInfo);

        var props = ReadProps(propsPath);
        if (props is null)
        {
            if (reported.Add($"parse{propsPath}"))
            {
                issues.Add(
                    new AnalysisIssue(
                        propsFile,
                        $"{propsFile} exists but could not be parsed as XML, so central versions "
                            + "cannot be verified.",
                        Array.Empty<string>(),
                        AnalysisIssueCode.CpmNotEnabled,
                        AnalysisSeverity.High,
                        Metadata: PropsMetadata(propsFile)
                    )
                );
            }

            return false;
        }

        if (IsCpmEnabled(props, propsPath, propertyRoot, out var enablement))
        {
            return true;
        }

        if (reported.Add($"disabled{propsPath}{enablement}"))
        {
            AddCpmNotEnabledIssue(issues, propsFile, enablement);
        }

        // With central management off, an inline version is not overriding anything — it is simply
        // how every project beneath this file declares its packages, so the remaining rules would
        // report the entire dependency list as drift.
        return false;
    }

    /// <summary>
    /// The props file as a reader should see it: relative to the scan root, so it names which file
    /// needs fixing when a repository has several, and stays identical on every machine.
    /// </summary>
    private static string DescribePropsPath(string propsPath, ProjectPackageInfo packageInfo)
    {
        return DescribePropsPath(propsPath, packageInfo.BasePath);
    }

    /// <inheritdoc cref="DescribePropsPath(string, ProjectPackageInfo)" />
    private static string DescribePropsPath(string propsPath, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return PropsFileName;
        }

        var relative = Path.GetRelativePath(basePath, Path.GetFullPath(propsPath));
        return ProjectPackageInfo.EscapesRoot(relative)
            ? PropsFileName
            : relative.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
    }

    /// <summary>
    /// Carries the props file into the finding's identity. Without it every unparseable or disabled
    /// file hashes the same — package fixed to the file name, no affected projects — so a baseline
    /// accepting one would silently suppress the others.
    /// </summary>
    private static Dictionary<string, string>? PropsMetadata(string propsFile)
    {
        // Nothing for the conventional root file. It is the only one that could produce a finding
        // before nested files were read, so adding a key would change every stored fingerprint for
        // it: a committed baseline would stop matching the High finding it had accepted, and SARIF
        // would reopen it, on upgrade and with no scheme change to explain why.
        return string.Equals(propsFile, PropsFileName, StringComparison.OrdinalIgnoreCase)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["propsFile"] = propsFile };
    }

    /// <summary>One props file, what it pins, and the scanned projects it governs.</summary>
    private sealed record CentralContext(
        XDocument Props,
        Dictionary<string, CentralEntry> Central,
        bool ImportsResolved
    )
    {
        public List<string> Projects { get; } = [];

        /// <summary>Directories the governed projects live in, for build-property lookups.</summary>
        public HashSet<string> PropertyRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reports a props file that exists without central management actually switched on, which
    /// leaves every <c>PackageVersion</c> entry inert — the file looks authoritative and does
    /// nothing.
    /// </summary>
    private static bool IsCpmEnabled(
        XDocument props,
        string propsPath,
        string? basePath,
        out string? enablement
    )
    {
        enablement = ReadCpmEnablementThroughImports(
            props,
            propsPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );

        if (string.Equals(enablement, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (enablement is null)
        {
            // MSBuild resolves the property through imports, and Directory.Build.props is the other
            // conventional home for it. Absence from both is still not proof — a custom import could
            // set it — but reporting on the props file alone produces a High-severity false positive
            // on repositories that are perfectly well configured.
            var buildProps = ReadNearestBuildProps(propsPath, basePath);

            if (buildProps is not null && ReadCpmEnablement(buildProps) is { } inherited)
            {
                if (string.Equals(inherited, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                enablement = inherited;
            }
        }

        return false;
    }

    private static void AddCpmNotEnabledIssue(
        List<AnalysisIssue> issues,
        string propsFile,
        string? enabled
    )
    {
        issues.Add(
            new AnalysisIssue(
                propsFile,
                enabled is null
                    ? $"{propsFile} exists but does not set ManagePackageVersionsCentrally, so "
                        + "its PackageVersion entries are ignored."
                    : $"{propsFile} sets ManagePackageVersionsCentrally to '{enabled}', so its "
                        + "PackageVersion entries are ignored.",
                Array.Empty<string>(),
                AnalysisIssueCode.CpmNotEnabled,
                AnalysisSeverity.High,
                Metadata: PropsMetadata(propsFile)
            )
        );
    }

    /// <summary>
    /// True when transitive pinning is on, in which case a <c>PackageVersion</c> may deliberately pin
    /// a package no project references directly — so every such pin would look orphaned.
    /// </summary>
    private static bool IsTransitivePinningEnabled(
        XDocument props,
        string propsPath,
        string? basePath
    )
    {
        if (IsPropertyTrue(props, "CentralPackageTransitivePinningEnabled"))
        {
            return true;
        }

        // The same resolution the enablement check uses. Reading only the scan root meant a nested
        // project whose Directory.Build.props turns transitive pinning on had its deliberately
        // transitive-only pins reported as OrphanedPackageVersion.
        var buildProps = ReadNearestBuildProps(propsPath, basePath);

        return buildProps is not null
            && IsPropertyTrue(buildProps, "CentralPackageTransitivePinningEnabled");
    }

    /// <summary>
    /// Last-wins value of a boolean property, matching how MSBuild resolves a repeated assignment.
    /// Accepting any earlier <c>true</c> would ignore a later override to <c>false</c>.
    /// </summary>
    private static bool IsPropertyTrue(XDocument document, string propertyName)
    {
        var effective = document
            .Descendants()
            .Where(e => e.Name.LocalName == propertyName)
            .Select(e => e.Value.Trim())
            .LastOrDefault();

        return string.Equals(effective, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Last-wins value of <c>ManagePackageVersionsCentrally</c> in a document, or null when absent.
    /// </summary>
    private static string? ReadCpmEnablement(XDocument document)
    {
        return document
            .Descendants()
            .Where(e => e.Name.LocalName == "ManagePackageVersionsCentrally")
            .Select(e => e.Value.Trim())
            .LastOrDefault();
    }

    /// <summary>
    /// Checks one project for references that bypass or contradict the central file.
    /// </summary>
    private static void InspectProject(
        List<AnalysisIssue> issues,
        ProjectPackageInfo packageInfo,
        string projectPath,
        IReadOnlyDictionary<string, CentralEntry> central,
        HashSet<string> referenced,
        bool importsResolved
    )
    {
        var project = ReadProps(projectPath);
        if (project is null)
        {
            return;
        }

        var projectId = packageInfo.ProjectId(projectPath);

        foreach (var element in project.Descendants().Where(IsPackageReference))
        {
            var packageName =
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            referenced.Add(packageName);

            var hasCentralVersion =
                central.TryGetValue(packageName, out var centralEntry)
                && centralEntry.Version is not null;

            var overrideVersion = ReadAttributeOrChild(element, "VersionOverride");
            if (overrideVersion is not null)
            {
                // VersionOverride is NuGet's supported per-project escape hatch, so this is not a
                // mistake the way a stray Version attribute is — but the project has still stepped
                // outside the central version, which is what a reviewer needs to see. Lower severity
                // accordingly: it is deliberate.
                issues.Add(
                    new AnalysisIssue(
                        packageName,
                        $"Uses VersionOverride=\"{overrideVersion}\" to step outside the central "
                            + (hasCentralVersion ? $"{centralEntry.Version}." : "version.")
                            + " Intentional, but the project no longer follows the solution.",
                        new[] { projectId },
                        AnalysisIssueCode.InlineVersionUnderCpm,
                        AnalysisSeverity.Low,
                        Fixable: false
                    )
                );

                continue;
            }

            var inlineVersion = ReadVersion(element);
            if (inlineVersion is not null)
            {
                issues.Add(
                    new AnalysisIssue(
                        packageName,
                        hasCentralVersion
                            ? $"Declares Version=\"{inlineVersion}\" inline, overriding the central "
                                + $"{centralEntry.Version}. Remove the attribute so the central version applies."
                            : $"Declares Version=\"{inlineVersion}\" inline instead of centrally. "
                                + $"Move it to {PropsFileName}.",
                        new[] { projectId },
                        AnalysisIssueCode.InlineVersionUnderCpm,
                        AnalysisSeverity.Moderate,
                        Fixable: false
                    )
                );

                continue;
            }

            // A central entry with an empty Version supplies nothing usable, so a reference relying
            // on it still breaks restore. Only assert that when the central set is complete.
            if (!hasCentralVersion && importsResolved)
            {
                issues.Add(
                    new AnalysisIssue(
                        packageName,
                        $"Referenced with no version: neither an inline Version nor a PackageVersion "
                            + $"entry in {PropsFileName}. Restore will fail.",
                        new[] { projectId },
                        AnalysisIssueCode.MissingPackageVersion,
                        AnalysisSeverity.High,
                        Fixable: false
                    )
                );
            }
        }
    }

    /// <summary>
    /// Reports central entries nothing references. Harmless to restore, but they accumulate, and a
    /// stale pin is indistinguishable from a deliberate one when someone comes to upgrade.
    /// </summary>
    private static void AddOrphanedVersionIssues(
        List<AnalysisIssue> issues,
        IReadOnlyDictionary<string, CentralEntry> central,
        HashSet<string> referenced,
        string propsFile
    )
    {
        foreach (
            var (packageName, entry) in central.OrderBy(
                e => e.Key,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            if (referenced.Contains(packageName))
            {
                continue;
            }

            issues.Add(
                new AnalysisIssue(
                    packageName,
                    $"Pinned at {entry.Version ?? "an unspecified version"} in {propsFile} but "
                        + "referenced by no project. Remove it, or the pin outlives what it was for.",
                    Array.Empty<string>(),
                    AnalysisIssueCode.OrphanedPackageVersion,
                    AnalysisSeverity.Low,
                    Fixable: false,
                    // Names which file to edit when a repository has several, and keeps two files
                    // orphaning the same package from sharing one identity — a baseline would
                    // otherwise record one and suppress both.
                    Metadata: PropsMetadata(propsFile)
                )
            );
        }
    }

    /// <summary>
    /// Central versions by package ID. <c>GlobalPackageReference</c> counts: it supplies a version
    /// centrally too, so a project referencing such a package is not missing anything.
    /// </summary>
    /// <summary>
    /// Collects central versions from a props file and everything it imports.
    ///
    /// Returns whether every import could be followed. An import path built from MSBuild properties
    /// or a glob cannot be resolved by reading XML, and when that happens the central set is
    /// incomplete — so the caller must not conclude a reference is unversioned.
    /// </summary>
    private static (
        Dictionary<string, CentralEntry> Central,
        bool ImportsResolved
    ) ReadCentralVersions(XDocument props, string propsPath)
    {
        var versions = new Dictionary<string, CentralEntry>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = CollectCentralVersions(props, propsPath, versions, visited);

        return (versions, resolved);
    }

    private static bool CollectCentralVersions(
        XDocument document,
        string documentPath,
        Dictionary<string, CentralEntry> versions,
        HashSet<string> visited
    )
    {
        if (!visited.Add(Path.GetFullPath(documentPath)))
        {
            // Circular imports are legal in MSBuild (it de-duplicates); re-reading would not add
            // anything and would not terminate.
            return true;
        }

        var allResolved = true;

        foreach (var element in document.Descendants())
        {
            var isPackageVersion = element.Name.LocalName.Equals(
                PackageVersionItem,
                StringComparison.OrdinalIgnoreCase
            );
            var isGlobal = element.Name.LocalName.Equals(
                GlobalPackageReferenceItem,
                StringComparison.OrdinalIgnoreCase
            );

            if (element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase))
            {
                allResolved &= FollowImport(element, documentPath, versions, visited);
                continue;
            }

            if (!isPackageVersion && !isGlobal)
            {
                continue;
            }

            var packageName =
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (!string.IsNullOrWhiteSpace(packageName))
            {
                versions[packageName] = new CentralEntry(ReadVersion(element), isGlobal);
            }
        }

        return allResolved;
    }

    /// <summary>
    /// Follows one <c>Import</c>. Returns false when the path cannot be resolved by reading XML —
    /// an MSBuild property or a glob — since the central set is then incomplete.
    /// </summary>
    private static bool FollowImport(
        XElement import,
        string documentPath,
        Dictionary<string, CentralEntry> versions,
        HashSet<string> visited
    )
    {
        var relative = import.Attribute("Project")?.Value;
        if (string.IsNullOrWhiteSpace(relative))
        {
            return true;
        }

        if (relative.Contains("$(", StringComparison.Ordinal) || relative.Contains('*'))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        if (directory is null)
        {
            return false;
        }

        var importedPath = Path.GetFullPath(
            Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar))
        );
        var imported = ReadProps(importedPath);

        if (imported is null)
        {
            // A conditional import of a file that is not there is normal, so this is not treated as
            // an unresolved import.
            return !File.Exists(importedPath)
                || string.IsNullOrEmpty(import.Attribute("Condition")?.Value);
        }

        return CollectCentralVersions(imported, importedPath, versions, visited);
    }

    /// <summary>
    /// A central version entry, and whether it came from <c>GlobalPackageReference</c> — which
    /// applies to every project implicitly, so it is never orphaned.
    /// </summary>
    private readonly record struct CentralEntry(string? Version, bool IsGlobal);

    private static bool IsPackageReference(XElement element)
    {
        return element.Name.LocalName.Equals(
            PackageReferenceItem,
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Reads a version from either the attribute or the child-element form, both of which MSBuild
    /// accepts. Returns null when absent or empty — an empty <c>Version=""</c> does not override a
    /// central version, so treating it as inline would be a false positive.
    /// </summary>
    private static string? ReadVersion(XElement element)
    {
        return ReadAttributeOrChild(element, "Version");
    }

    /// <summary>
    /// Reads a value from either the attribute or the child-element form, both of which MSBuild
    /// accepts. Returns null when absent or empty — an empty value overrides nothing, so treating it
    /// as present would be a false positive.
    /// </summary>
    private static string? ReadAttributeOrChild(XElement element, string name)
    {
        var attribute = element.Attribute(name)?.Value;
        if (!string.IsNullOrWhiteSpace(attribute))
        {
            return attribute.Trim();
        }

        var child = element
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(child) ? null : child.Trim();
    }

    /// <summary>
    /// The central pins in effect for a scan, as package id to the version specification exactly as
    /// written — following imports, accepting the <c>Update</c> form and child-element
    /// <c>&lt;Version&gt;</c> metadata, both of which MSBuild accepts and this repository's own props
    /// file uses.
    ///
    /// <para>
    /// Exposed so other rules read central versions through the same parser rather than a second,
    /// simpler one. A rule with its own reader silently misses whichever forms it did not think of,
    /// and reports the solution clean — which is indistinguishable from a solution with nothing
    /// wrong.
    /// </para>
    ///
    /// <para>
    /// Empty when the solution is not centrally managed, including when the props file exists but
    /// sets <c>ManagePackageVersionsCentrally</c> to false: those <c>PackageVersion</c> items are
    /// inert, so treating them as effective versions would report a finding about a value NuGet
    /// ignores.
    /// </para>
    ///
    /// <para>
    /// Every props file governing a scanned project contributes, not only the one at the scan root:
    /// a repository can hold several, each governing the projects beneath it, and a caller reading
    /// only the root file would miss whatever a nested one pins.
    /// </para>
    /// </summary>
    /// <param name="basePath">Directory the scan was rooted at.</param>
    /// <param name="projectPaths">
    /// Projects in the scan. When empty, only the scan root's props file is consulted.
    /// </param>
    internal static IReadOnlyList<CentralPin> ReadEffectiveCentralVersions(
        string? basePath,
        IEnumerable<string>? projectPaths = null
    )
    {
        var effective = new List<CentralPin>();

        var directories = (projectPaths ?? [])
            .Select(project => Path.GetDirectoryName(Path.GetFullPath(project)))
            .Where(directory => directory is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories.Count == 0)
        {
            directories.Add(basePath);
        }

        // The directory is carried alongside its props file, not replaced by the scan root: whether
        // that file's pins are in force depends on properties resolved from the project's own
        // directory, so a repository disabling central management at the root and enabling it under
        // tools/ would otherwise have the nested pins read as inert.
        foreach (
            var resolved in directories
                .Select(directory => (Directory: directory, Props: ResolvePropsPath(directory)))
                .Where(entry => entry.Props is not null)
                .DistinctBy(
                    entry => $"{entry.Props}{KeySeparator}{entry.Directory}",
                    StringComparer.OrdinalIgnoreCase
                )
                .OrderBy(entry => entry.Props, StringComparer.Ordinal)
        )
        {
            AddEffectiveCentralVersions(
                resolved.Props!,
                resolved.Directory,
                effective,
                DescribePropsPath(resolved.Props!, basePath)
            );
        }

        return effective;
    }

    private static void AddEffectiveCentralVersions(
        string propsPath,
        string? basePath,
        List<CentralPin> effective,
        string propsFile
    )
    {
        var props = ReadProps(propsPath);
        if (props is null || !IsCpmEnabled(props, propsPath, basePath, out _))
        {
            return;
        }

        var (central, _) = ReadCentralVersions(props, propsPath);

        foreach (
            var entry in central.Where(entry => !string.IsNullOrWhiteSpace(entry.Value.Version))
        )
        {
            // Every distinct pin, not one per package. Collapsing by package let a root file's exact
            // pin hide a nested file's floating one — the nested project's dependency would then
            // pass as reproducible when it is not, which is the failure this whole rule exists to
            // catch.
            var pin = new CentralPin(entry.Key, entry.Value.Version!, propsFile);
            if (!effective.Contains(pin))
            {
                effective.Add(pin);
            }
        }
    }

    /// <summary>One central pin: a package, the specification verbatim, and where it was written.</summary>
    /// <param name="Package">Package id.</param>
    /// <param name="Version">The specification, verbatim.</param>
    /// <param name="PropsFile">The props file the pin was read from, relative to the scan root.</param>
    internal readonly record struct CentralPin(string Package, string Version, string PropsFile);

    /// <summary>
    /// Finds the <c>Directory.Packages.props</c> in effect, walking up from the scan root the way
    /// MSBuild does — the nearest one wins.
    ///
    /// <para>
    /// Looking only at the scan root meant pointing <c>--analyze</c> at one solution inside a
    /// repository reported it as having no central versions at all: every CPM rule went quiet, which
    /// is what a solution with nothing wrong also looks like.
    /// </para>
    ///
    /// <para>
    /// The walk stops at the repository root rather than continuing to the filesystem root. MSBuild
    /// would keep going, but a props file in a parent of the checkout belongs to something else, and
    /// letting an unrelated file on one machine decide what a scan reports makes the result
    /// unreproducible on any other.
    /// </para>
    /// </summary>
    private static string? ResolvePropsPath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(basePath));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, PropsFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (IsRepositoryRoot(directory.FullName))
            {
                // Checked after the candidate, so a props file sitting at the repository root is
                // still found — it is the last directory searched, not the first one skipped.
                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// The enablement property as MSBuild would resolve it, following imports.
    ///
    /// <para>
    /// Reading only the props file itself called a repository unmanaged whenever the property lived
    /// in a file the props file imports — a perfectly ordinary way to organise it — and that answer
    /// is loud: every central pin becomes inert, so the CPM rules report a High-severity finding
    /// about a repository that is correctly configured.
    /// </para>
    ///
    /// <para>
    /// Elements are read in document order and the last assignment wins, which is how MSBuild
    /// evaluates a file: a property set after an <c>Import</c> overrides what the import set. Taking
    /// the local value first would have let an import turn central management on but never off.
    /// </para>
    ///
    /// <para>
    /// An <c>Import</c> carrying a <c>Condition</c> is skipped rather than followed. Whether it
    /// applies depends on properties this cannot evaluate, and an inactive import that switched
    /// central management on would be worse than not reading it at all — the drift rules would then
    /// judge every project against pins NuGet never applies.
    /// </para>
    ///
    /// <para>
    /// A conditioned <em>assignment</em> is still read, and that is a deliberate choice rather than
    /// an oversight. Treating it as unresolved would be the more faithful reading of MSBuild, but
    /// the commonest use of a condition here is defaulting
    /// (<c>Condition="'$(ManagePackageVersionsCentrally)' == ''"</c>), and ignoring those would
    /// report <c>CpmNotEnabled</c> — a High finding that fails CI — across a great many repositories
    /// that are configured perfectly well. The cost is the opposite error on a genuinely
    /// configuration-specific assignment. Both readings are wrong somewhere; this one is wrong less
    /// often, and it errs towards not accusing a working repository.
    /// </para>
    /// </summary>
    private static string? ReadCpmEnablementThroughImports(
        XDocument document,
        string documentPath,
        HashSet<string> visited
    )
    {
        if (!visited.Add(Path.GetFullPath(documentPath)))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        string? resolved = null;

        // Document order, last assignment wins — how MSBuild evaluates a file. Reading the local
        // value first instead would let an import turn central management on but never off.
        foreach (var element in document.Descendants())
        {
            if (
                element.Name.LocalName.Equals(
                    EnablementProperty,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                resolved = element.Value.Trim();
                continue;
            }

            if (
                !element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase)
                || directory is null
            )
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(element.Attribute("Condition")?.Value))
            {
                // Whether it applies depends on properties this cannot evaluate. Following it could
                // switch central management on where NuGet leaves it off, and the drift rules would
                // then judge every project against pins that are never applied.
                continue;
            }

            var relative = element.Attribute("Project")?.Value;
            if (
                string.IsNullOrWhiteSpace(relative)
                || relative.Contains("$(", StringComparison.Ordinal)
                || relative.Contains('*')
            )
            {
                // Built from MSBuild properties or a glob: not resolvable by reading XML.
                continue;
            }

            var importedPath = Path.GetFullPath(
                Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar))
            );
            var imported = ReadProps(importedPath);
            if (imported is null)
            {
                continue;
            }

            if (ReadCpmEnablementThroughImports(imported, importedPath, visited) is { } inherited)
            {
                resolved = inherited;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Whether a directory is the root of a working tree.
    ///
    /// <c>.git</c> is a directory in an ordinary clone but a <em>file</em> in a linked worktree or a
    /// submodule. Testing only for the directory walked straight past those roots, so a props file
    /// in a parent could be picked up — the machine-dependent result this boundary exists to
    /// prevent, appearing only for the people using worktrees.
    /// </summary>
    private static bool IsRepositoryRoot(string directory)
    {
        var git = Path.Combine(directory, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>
    /// The nearest <c>Directory.Build.props</c>: beside the central props file first, then beside
    /// the scan root. Both are conventional homes for the enablement property.
    ///
    /// Once the props file can come from an ancestor, looking only beside the scan root means a
    /// nested solution misses the <c>Directory.Build.props</c> that sits next to the props file —
    /// and reports <c>CpmNotEnabled</c>, a High finding, on a repository that is correctly set up.
    /// </summary>
    /// <param name="propsPath">Path of the central props file that was found.</param>
    /// <param name="basePath">Directory the scan was rooted at.</param>
    private static XDocument? ReadNearestBuildProps(string propsPath, string? basePath)
    {
        // From the scan root upwards first, because that is where MSBuild starts and the nearest
        // file wins: with /repo/Directory.Build.props setting the property one way and
        // /repo/src/Directory.Build.props setting it the other, a project under src gets the
        // nearer answer. Checking only the two endpoints picked the wrong one.
        var fromScanRoot = WalkUpForBuildProps(basePath);
        if (fromScanRoot is not null)
        {
            return fromScanRoot;
        }

        // Then beside the props file, which may sit above the scan root and above the boundary the
        // walk stops at.
        return WalkUpForBuildProps(
            Path.GetDirectoryName(Path.GetFullPath(propsPath)),
            single: true
        );
    }

    /// <summary>
    /// The nearest <c>Directory.Build.props</c> at or above a directory, stopping at the repository
    /// root for the same reason the central props walk does.
    /// </summary>
    /// <param name="startDirectory">Where to start looking.</param>
    /// <param name="single">When true, look only in <paramref name="startDirectory"/>.</param>
    private static XDocument? WalkUpForBuildProps(string? startDirectory, bool single = false)
    {
        const string BuildPropsFileName = "Directory.Build.props";

        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var found = ReadProps(Path.Combine(directory.FullName, BuildPropsFileName));
            if (found is not null)
            {
                return found;
            }

            if (single || IsRepositoryRoot(directory.FullName))
            {
                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static XDocument? ReadProps(string path)
    {
        try
        {
            return File.Exists(path) ? XDocument.Load(path) : null;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
