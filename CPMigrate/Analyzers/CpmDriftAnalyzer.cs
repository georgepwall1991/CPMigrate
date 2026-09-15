using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services;

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

    private const string TransitivePinningProperty = "CentralPackageTransitivePinningEnabled";

    /// <summary>Unit separator: it cannot occur in a path, so composite keys stay unambiguous.</summary>
    private const string KeySeparator = "\u001F";

    /// <inheritdoc />
    public string Name => "Central Package Management Drift";

    /// <inheritdoc />
    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);
        return Analyze(packageInfo, MsBuildProps.PathComparerFor(packageInfo.BasePath));
    }

    /// <summary>
    /// Analyzes a package scan with an explicit path comparer. The overload is internal so tests can
    /// exercise case-distinct contexts even on a case-insensitive host; production scans always use
    /// <see cref="MsBuildProps.PathComparerFor(string?)"/> for their scan root.
    /// </summary>
    internal AnalyzerResult Analyze(ProjectPackageInfo packageInfo, StringComparer pathComparer)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);
        ArgumentNullException.ThrowIfNull(pathComparer);

        var issues = new List<AnalysisIssue>();

        // Grouped by the props file that actually governs each project, because MSBuild resolves
        // Directory.Packages.props from the project's own directory. Judging every project against
        // one set is wrong in both directions: a project under a nested props file gets measured
        // against pins it never sees — MissingPackageVersion, a High finding, on references that
        // restore perfectly well — and a pin is called orphaned because the projects using it were
        // reading a different file.
        var governed = GroupProjectsByGoverningProps(packageInfo, issues, pathComparer);

        // Collected across every context and judged once at the end. A nested props file can import
        // an ancestor, so the same pin appears in two central sets — and a pin used only by projects
        // under the *other* file would be reported orphaned by this one, and the other way round.
        var orphanCandidates =
            new List<(
                IReadOnlyDictionary<string, CentralEntry> Central,
                HashSet<string> Referenced,
                bool CanOriginate
            )>();

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
                    context.ImportsResolved,
                    DescribePropsPath(propsPath, packageInfo)
                );
            }

            // Both remaining rules compare against the full central set. If an import could not be
            // followed, that set is incomplete, and every conclusion drawn from it would be a guess.
            // Asked from each governed project's directory, and satisfied by any of them.
            // Transitive pinning makes a pin that nothing references deliberate, so treating it as
            // on wherever it might be errs towards not accusing a project that is fine.
            var transitivePinning =
                context.PropertyRoots.Count == 0
                    ? IsTransitivePinningEnabled(
                        context.Props,
                        propsPath,
                        packageInfo.BasePath,
                        pathComparer
                    )
                    : context.PropertyRoots.Any(root =>
                        IsTransitivePinningEnabled(context.Props, propsPath, root, pathComparer)
                    );

            // Added whatever the answer: the references are evidence for every pin this context
            // holds. Only the flag decides whether the context may state an orphan of its own.
            orphanCandidates.Add(
                (context.Central, referenced, context.ImportsResolved && !transitivePinning)
            );
        }

        AddOrphanedVersionIssues(issues, orphanCandidates, packageInfo, pathComparer);

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
        List<AnalysisIssue> issues,
        StringComparer pathComparer
    )
    {
        var usable = new Dictionary<string, CentralContext>(pathComparer);
        // Keyed by props file *and* the directory the properties were resolved from, because two
        // projects can share a props file while a nearer Directory.Build.props enables central
        // management for one and not the other. Caching by props file alone let whichever project
        // came first decide for both.
        var enablement = new Dictionary<string, bool>(pathComparer);
        var reported = new HashSet<string>(pathComparer);
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
            var propsPath = ResolvePropsPath(directory, pathComparer);
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

            // The project's own body settles it. MSBuild evaluates it after Directory.Build.props,
            // and setting the property in a single .csproj is the documented way to opt one project
            // out of — or into — central management. Judging it by the surrounding files alone
            // reported every ordinary inline version in an opted-out project as drift, and skipped
            // an opted-in one as CpmNotEnabled.
            var projectEnablement = project is null
                ? null
                : ReadProjectEnablement(project, pathComparer);

            if (string.Equals(projectEnablement, "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var optedIn = string.Equals(
                projectEnablement,
                "true",
                StringComparison.OrdinalIgnoreCase
            );

            // The opt-in is part of the key: the same file can be inert for its neighbours and in
            // force for a project that turns it on, and one cached answer cannot say both.
            var enablementKey = $"{propsPath}{KeySeparator}{propertyRoot}{KeySeparator}{optedIn}";

            if (!enablement.TryGetValue(enablementKey, out var enabled))
            {
                enabled = IsUsable(
                    propsPath,
                    propertyRoot,
                    packageInfo,
                    issues,
                    reported,
                    optedIn,
                    pathComparer
                );
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
                var props = MsBuildProps.ReadProps(propsPath)!;
                var (central, _, importsResolved) = ReadCentralVersions(
                    props,
                    propsPath,
                    pathComparer
                );
                context = new CentralContext(props, central, importsResolved, pathComparer);
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
        HashSet<string> reported,
        // The project turned central management on for itself, so the surrounding files no longer
        // get to call the file inert. It must still parse.
        bool optedIn,
        StringComparer pathComparer
    )
    {
        var propsFile = DescribePropsPath(propsPath, packageInfo);

        var props = MsBuildProps.ReadProps(propsPath);
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

        if (
            IsCpmEnabled(props, propsPath, propertyRoot, out var enablement, pathComparer)
                || optedIn
        )
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
        var normalized = relative.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

        // The path is kept even when it escapes the scan root. Collapsing to the conventional name
        // was only ever safe while one scan could reach a single file above its root; a redirect
        // reaches any file, and two subtrees can redirect to different files that happen to share
        // the conventional name. Both would then name neither actual path and carry one identity,
        // so a baseline accepting one would silently suppress the other. A '../' prefix is the
        // honest answer: the file really is outside the scan, and the path stays relative, so it is
        // still identical on every machine.
        return normalized;
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
        bool ImportsResolved,
        StringComparer PathComparer
    )
    {
        public List<string> Projects { get; } = [];

        /// <summary>
        /// Directories the governed projects live in, for build-property lookups. Compared the
        /// way the scan root's filesystem compares them, so case-distinct directories stay distinct
        /// wherever the filesystem keeps them apart.
        /// </summary>
        public HashSet<string> PropertyRoots { get; } = new(PathComparer);
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
        out string? enablement,
        StringComparer pathComparer
    )
    {
        enablement = MsBuildProps.ReadPropertyThroughImports(
            props,
            propsPath,
            EnablementProperty,
            new HashSet<string>(pathComparer),
            pathComparer
        )?.Value;

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
            //
            // Read through the build props' own imports too. A fragment supplying both the redirect
            // and the enablement is an ordinary arrangement, and reading only the outer document
            // reported CpmNotEnabled on a repository MSBuild manages.
            var buildProps = ReadNearestBuildProps(propsPath, basePath);

            if (
                buildProps is { } nearest
                && MsBuildProps.ReadPropertyThroughImports(
                    nearest.Document,
                    nearest.Path,
                    EnablementProperty,
                    new HashSet<string>(pathComparer),
                    pathComparer
                )
                    is { } inherited
            )
            {
                if (string.Equals(inherited.Value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                enablement = inherited.Value;
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
                // Fixable in the common case — the props file parses and only needs the property
                // set. The fixer refuses when projects still declare Version inline, because
                // enabling CPM over them turns each into NU1008; that is --migrate's job.
                Fixable: true,
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
        string? basePath,
        StringComparer pathComparer
    )
    {
        if (IsPropertyTrue(props, propsPath, TransitivePinningProperty, pathComparer))
        {
            return true;
        }

        // The same resolution the enablement check uses. Reading only the scan root meant a nested
        // project whose Directory.Build.props turns transitive pinning on had its deliberately
        // transitive-only pins reported as OrphanedPackageVersion.
        var buildProps = ReadNearestBuildProps(propsPath, basePath);

        return buildProps is { } nearest
            && IsPropertyTrue(
                nearest.Document,
                nearest.Path,
                TransitivePinningProperty,
                pathComparer
            );
    }

    /// <summary>
    /// Last-wins value of a boolean property, matching how MSBuild resolves a repeated assignment.
    /// Accepting any earlier <c>true</c> would ignore a later override to <c>false</c>.
    ///
    /// <para>
    /// Read through imports on the same terms as every other property. A repository that turns
    /// transitive pinning on from an imported fragment would otherwise have its deliberately
    /// transitive-only pins reported as orphaned.
    /// </para>
    /// </summary>
    private static bool IsPropertyTrue(
        XDocument document,
        string documentPath,
        string propertyName,
        StringComparer pathComparer
    )
    {
        var effective = MsBuildProps.ReadPropertyThroughImports(
            document,
            documentPath,
            propertyName,
            new HashSet<string>(pathComparer),
            pathComparer
        )?.Value;

        return string.Equals(effective, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The project's own <c>ManagePackageVersionsCentrally</c>, or null when it does not set one.
    ///
    /// <para>
    /// Read through the project's imports, like every other build property: a project delegating
    /// its settings to a fragment is no different to MSBuild than one setting them inline, and
    /// reading only the project's own descendants judged an imported opt-out centrally managed.
    /// </para>
    ///
    /// <para>
    /// An unreadable project is treated as silent rather than as an opt-out, so a file this analyzer
    /// cannot parse never suppresses findings.
    /// </para>
    /// </summary>
    private static string? ReadProjectEnablement(
        string projectPath,
        StringComparer pathComparer
    )
    {
        var project = MsBuildProps.ReadProps(projectPath);

        return project is null
            ? null
            : MsBuildProps.ReadPropertyThroughImports(
                project,
                projectPath,
                EnablementProperty,
                new HashSet<string>(pathComparer),
                pathComparer
            )?.Value;
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
        bool importsResolved,
        // The file MSBuild actually reads for this project. Naming the root one told a project
        // governed by a nested props file to edit a file MSBuild never consults for it.
        string propsFile
    )
    {
        var project = MsBuildProps.ReadProps(projectPath);
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
                                + $"Move it to {propsFile}.",
                        new[] { projectId },
                        AnalysisIssueCode.InlineVersionUnderCpm,
                        AnalysisSeverity.Moderate,
                        Fixable: true
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
                            + $"entry in {propsFile}. Restore will fail.",
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
    /// <summary>
    /// Reports central pins no scanned project references.
    ///
    /// <para>
    /// Judged across every governing props file at once, and attributed to the file that
    /// <em>declared</em> each pin. A nested props file may import an ancestor, so one pin can appear
    /// in two central sets — reporting per set would call it orphaned in the file whose projects
    /// happen not to use it while another file's projects do, and would report an inherited pin once
    /// per file that imports it.
    /// </para>
    /// </summary>
    private static void AddOrphanedVersionIssues(
        List<AnalysisIssue> issues,
        List<(
            IReadOnlyDictionary<string, CentralEntry> Central,
            HashSet<string> Referenced,
            bool CanOriginate
        )> candidates,
        ProjectPackageInfo packageInfo,
        StringComparer pathComparer
    )
    {
        var pins = new Dictionary<string, (CentralEntry Entry, string Package)>(pathComparer);

        // Evidence is pooled only across the contexts that actually hold a given pin, not across
        // every context. A reference under a nested file that does *not* import the root is no
        // evidence at all that the root's pin is used — counting it would suppress a real orphan.
        var evidence = new Dictionary<string, HashSet<string>>(pathComparer);

        foreach (var (central, referenced, canOriginate) in candidates)
        {
            foreach (var (packageName, entry) in central)
            {
                var key = $"{entry.SourcePath}{KeySeparator}{packageName}";

                // Whether a context may *originate* a finding is separate from whether its
                // references count as evidence. A context with transitive pinning on states no
                // orphans of its own, but a package its projects reference is still in use — losing
                // that proof reported the pin orphaned against the file that declares it.
                if (canOriginate)
                {
                    pins.TryAdd(key, (entry, packageName));
                }

                if (!evidence.TryGetValue(key, out var seen))
                {
                    seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    evidence[key] = seen;
                }

                seen.UnionWith(referenced);
            }
        }

        foreach (
            var (key, (entry, packageName)) in pins.OrderBy(pin => pin.Key, StringComparer.Ordinal)
        )
        {
            if (evidence[key].Contains(packageName))
            {
                continue;
            }

            var propsFile = DescribePropsPath(entry.SourcePath, packageInfo);

            issues.Add(
                new AnalysisIssue(
                    packageName,
                    $"Pinned at {entry.Version ?? "an unspecified version"} in {propsFile} but "
                        + "referenced by no project. Remove it, or the pin outlives what it was for.",
                    Array.Empty<string>(),
                    AnalysisIssueCode.OrphanedPackageVersion,
                    AnalysisSeverity.Low,
                    Fixable: true,
                    // Names which file to edit when a repository has several, and keeps two files
                    // orphaning the same package from sharing one identity — a baseline would
                    // otherwise record one and suppress both.
                    Metadata: PropsMetadata(propsFile)
                )
            );
        }
    }

    private static (
        Dictionary<string, CentralEntry> Central,
        List<ConditionalCentralEntry> Conditional,
        bool ImportsResolved
    ) ReadCentralVersions(
        XDocument props,
        string propsPath,
        StringComparer pathComparer
    )
    {
        var versions = new Dictionary<string, CentralEntry>(StringComparer.OrdinalIgnoreCase);
        var conditional = new List<ConditionalCentralEntry>();
        var visited = new HashSet<string>(pathComparer);
        var resolved = CollectCentralVersions(
            props,
            propsPath,
            versions,
            conditional,
            visited,
            pathComparer
        );

        return (versions, conditional, resolved);
    }

    private static bool CollectCentralVersions(
        XDocument document,
        string documentPath,
        Dictionary<string, CentralEntry> versions,
        List<ConditionalCentralEntry> conditional,
        HashSet<string> visited,
        StringComparer pathComparer,
        bool inheritedConditional = false
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
                var conditionalImport = inheritedConditional || MsBuildProps.HasCondition(element);
                if (conditionalImport)
                {
                    // The file can still be inspected for declaration-level rules, but its pins
                    // cannot be universal evidence when the import may not apply.
                    allResolved = false;
                }

                allResolved &= FollowImport(
                    element,
                    documentPath,
                    versions,
                    conditional,
                    conditionalImport,
                    visited,
                    pathComparer
                );
                continue;
            }

            if (!isPackageVersion && !isGlobal)
            {
                continue;
            }

            var packageName =
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;

            if (inheritedConditional || MsBuildProps.HasCondition(element))
            {
                // A conditional pin may not exist in every evaluated configuration. Keep it in a
                // separate stream for rules such as FloatingVersion that can still inspect its
                // declaration, but do not merge it into the universal set used for drift conclusions.
                allResolved = false;
                if (!string.IsNullOrWhiteSpace(packageName))
                {
                    conditional.Add(
                        new ConditionalCentralEntry(
                            packageName,
                            new CentralEntry(ReadVersion(element), isGlobal, documentPath)
                        )
                    );
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(packageName))
            {
                versions[packageName] = new CentralEntry(
                    ReadVersion(element),
                    isGlobal,
                    documentPath,
                    ReadPrivateAssets(element)
                );
            }
        }

        return allResolved;
    }

    /// <summary>
    /// Follows one <c>Import</c>. Returns false when the path cannot be resolved by reading XML, or
    /// when a condition makes its applicability unknown, since the central set is then incomplete.
    /// </summary>
    private static bool FollowImport(
        XElement import,
        string documentPath,
        Dictionary<string, CentralEntry> versions,
        List<ConditionalCentralEntry> conditional,
        bool importIsConditional,
        HashSet<string> visited,
        StringComparer pathComparer
    )
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        if (
            directory is null
            || !MsBuildProps.TryResolveImportPath(
                import,
                directory,
                out var importedPath,
                allowConditional: true
            )
        )
        {
            return false;
        }

        var imported = MsBuildProps.ReadProps(importedPath);

        if (imported is null)
        {
            return false;
        }

        return CollectCentralVersions(
            imported,
            importedPath,
            versions,
            conditional,
            visited,
            pathComparer,
            inheritedConditional: importIsConditional
        );
    }

    /// <summary>
    /// A central version entry, and whether it came from <c>GlobalPackageReference</c> — which
    /// applies to every project implicitly, so it is never orphaned.
    /// </summary>
    /// <param name="Version">The specification as written, or null when the entry carries none.</param>
    /// <param name="IsGlobal">Whether it is a GlobalPackageReference, which every project gets.</param>
    /// <param name="SourcePath">
    /// The file that declared it, which is not always the file being read: a props file may import
    /// others, and a pin reported against the importing file names the wrong place to edit.
    /// </param>
    private readonly record struct CentralEntry(
        string? Version,
        bool IsGlobal,
        string SourcePath,
        string? PrivateAssets = null
    );

    private readonly record struct ConditionalCentralEntry(string Package, CentralEntry Entry);

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
    /// Reads a central item's <c>PrivateAssets</c> — attribute or child-element form — but only
    /// when nothing conditions it. Asset scoping set only for one configuration still lets the
    /// package flow to consumers on every other one, so a conditioned value is not coverage.
    /// </summary>
    private static string? ReadPrivateAssets(XElement element)
    {
        // Item metadata children evaluate after attributes, so they override one — the attribute
        // only counts when no child declares PrivateAssets. Among children the LAST decides
        // coverage: a trailing unconditional element overrides every earlier one, and a trailing
        // conditional one means coverage depends on the condition — which is not coverage.
        var children = element
            .Elements()
            .Where(e =>
                e.Name.LocalName.Equals("PrivateAssets", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (children.Count > 0)
        {
            var last = children[^1];
            return last.Attribute("Condition") is null && !string.IsNullOrWhiteSpace(last.Value)
                ? last.Value.Trim()
                : null;
        }

        var attribute = element.Attribute("PrivateAssets");
        return attribute is null || string.IsNullOrWhiteSpace(attribute.Value)
            ? null
            : attribute.Value.Trim();
    }

    /// <summary>
    /// Reads a value from either the attribute or the child-element form, both of which MSBuild
    /// accepts. Returns null when absent or empty — an empty value overrides nothing, so treating it
    /// as present would be a false positive.
    /// </summary>
    private static string? ReadAttributeOrChild(XElement element, string name)
    {
        // MSBuild evaluates item metadata children after attributes, and last-wins among the
        // children themselves — the effective value is the last child element, or the attribute
        // only when no child declares it.
        var child = element
            .Elements()
            .LastOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(child))
        {
            return child.Trim();
        }

        var attribute = element.Attribute(name)?.Value;
        return string.IsNullOrWhiteSpace(attribute) ? null : attribute.Trim();
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
    ///
    /// <para>
    /// Conditional central entries are retained here for rules that can inspect the declaration
    /// itself, such as <c>FloatingVersion</c>. The drift rules use the separate complete central set
    /// and mark the context incomplete instead of treating those entries as universally effective.
    /// </para>
    /// </summary>
    /// <param name="basePath">Directory the scan was rooted at.</param>
    /// <param name="projectPaths">
    /// Projects in the scan. When empty, only the scan root's props file is consulted.
    /// </param>
    internal static IReadOnlyList<CentralPin> ReadEffectiveCentralVersions(
        string? basePath,
        IEnumerable<string>? projectPaths = null,
        StringComparer? pathComparer = null
    )
    {
        var effective = new List<CentralPin>();
        pathComparer ??= MsBuildProps.PathComparerFor(basePath);

        var supplied = (projectPaths ?? []).ToList();

        // Each project's own enablement travels with its directory. Discarding the project path here
        // meant this reader could not see a project's opt-out, so pins inert for every project
        // governed by the file were still reported — and a floating pin in a file a project opted
        // into was still missed.
        var contexts = supplied
            .Select(project =>
                (
                    Directory: Path.GetDirectoryName(Path.GetFullPath(project)),
                    Enablement: ReadProjectEnablement(project, pathComparer)
                )
            )
            .Where(entry =>
                entry.Directory is not null
                && !string.Equals(entry.Enablement, "false", StringComparison.OrdinalIgnoreCase)
            )
            .Select(entry =>
                (
                    entry.Directory,
                    OptedIn: string.Equals(
                        entry.Enablement,
                        "true",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            .ToList();

        // Only when no project was supplied at all. A scan whose every project opted out has no
        // governed pins to read, and falling back to the scan root would invent them.
        if (supplied.Count == 0)
        {
            contexts.Add((basePath, false));
        }

        // The directory is carried alongside its props file, not replaced by the scan root: whether
        // that file's pins are in force depends on properties resolved from the project's own
        // directory, so a repository disabling central management at the root and enabling it under
        // tools/ would otherwise have the nested pins read as inert.
        foreach (
            var resolved in contexts
                .Select(entry =>
                    (
                        entry.Directory,
                        entry.OptedIn,
                        Props: ResolvePropsPath(entry.Directory, pathComparer)
                    )
                )
                .Where(entry => entry.Props is not null)
                .DistinctBy(
                    entry =>
                        $"{entry.Props}{KeySeparator}{entry.Directory}{KeySeparator}{entry.OptedIn}",
                    // The filesystem-aware comparer keeps case-distinct directories separate
                    // wherever they can hold different props files. Folding them here would drop
                    // whichever context came second — including its floating pins, leaving the
                    // scan reported clean.
                    pathComparer
                )
                .OrderBy(entry => entry.Props, StringComparer.Ordinal)
        )
        {
            AddEffectiveCentralVersions(
                resolved.Props!,
                resolved.Directory,
                basePath,
                resolved.OptedIn,
                effective,
                pathComparer
            );
        }

        return effective;
    }

    private static void AddEffectiveCentralVersions(
        string propsPath,
        string? propertyRoot,
        string? scanRoot,
        // The governed project turned central management on for itself, so the surrounding files
        // cannot call this file inert.
        bool optedIn,
        List<CentralPin> effective,
        StringComparer pathComparer
    )
    {
        var props = MsBuildProps.ReadProps(propsPath);
        if (
            props is null
            || (!IsCpmEnabled(props, propsPath, propertyRoot, out _, pathComparer) && !optedIn)
        )
        {
            return;
        }

        var (central, conditional, _) = ReadCentralVersions(props, propsPath, pathComparer);
        var allEntries = central
            .Select(entry => (Package: entry.Key, Entry: entry.Value, Conditional: false))
            .Concat(
                conditional.Select(entry => (entry.Package, entry.Entry, Conditional: true))
            );

        foreach (var entry in allEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.Entry.Version)))
        {
            // Every distinct pin, not one per package. Collapsing by package let a root file's exact
            // pin hide a nested file's floating one — the nested project's dependency would then
            // pass as reproducible when it is not, which is the failure this whole rule exists to
            // catch.
            // Attributed to the file that declared it, not the one that imported it: a pin
            // reported against the importing file names the wrong place to edit, and the same
            // inherited pin would be reported once per file that imports it.
            var pin = new CentralPin(
                entry.Package,
                entry.Entry.Version!,
                // Relative to the *scan root*, not the project directory the properties were
                // resolved from — otherwise a nested file reads as '../Directory.Packages.props'
                // and two different nested files collapse onto one name.
                DescribePropsPath(entry.Entry.SourcePath, scanRoot),
                // A pin that only exists for some configurations cannot scope assets for all of
                // them — asset coverage only counts when the entry is unconditional.
                entry.Conditional ? null : entry.Entry.PrivateAssets,
                entry.Entry.IsGlobal
            );
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
    /// <param name="PrivateAssets">
    /// The pin's unconditional <c>PrivateAssets</c> metadata, or null when absent or conditioned —
    /// a conditional scoping does not hold for every configuration, so it is not reported as coverage.
    /// </param>
    /// <param name="IsGlobal">
    /// Whether the entry is a <c>GlobalPackageReference</c>, which injects the reference into every
    /// governed project rather than merely pinning a version for whichever project asks.
    /// </param>
    internal readonly record struct CentralPin(
        string Package,
        string Version,
        string PropsFile,
        string? PrivateAssets = null,
        bool IsGlobal = false
    );

    /// <summary>
    /// Package references declared outside the project files but injected into them anyway: a
    /// <c>PackageReference Include</c> in a governing <c>Directory.Build.props</c>,
    /// <c>Directory.Build.targets</c>, or <c>Directory.Packages.props</c> applies to every project
    /// beneath the file, and the per-project declaration scan — which reads each project's own XML —
    /// never sees it. Rules about what a project declares would otherwise treat injected references
    /// as though they did not exist.
    ///
    /// <para>
    /// The walk mirrors MSBuild's import semantics: the nearest file of each name wins per project
    /// directory, and the walk stops at the repository root for the same reason the central props
    /// walk does. <c>GlobalPackageReference</c> items are skipped — they are already surfaced as
    /// pins — as are <c>Update</c>-only items, which amend rather than inject.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<InjectedPackageReference> ReadBuildImportPackageReferences(
        IEnumerable<string>? projectPaths,
        StringComparer? pathComparer = null
    )
    {
        var comparer = pathComparer ?? MsBuildProps.PathComparerFor(null);
        var files = new HashSet<string>(comparer);

        foreach (var projectPath in projectPaths ?? [])
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (directory is null)
            {
                continue;
            }

            foreach (var fileName in BuildImportFileNames)
            {
                if (WalkUpForFile(directory, fileName) is { } found)
                {
                    files.Add(found);
                }
            }

            if (ResolvePropsPath(directory, comparer) is { } packagesProps)
            {
                files.Add(packagesProps);
            }
        }

        var injected = new List<InjectedPackageReference>();
        foreach (var file in files)
        {
            var document = MsBuildProps.ReadProps(file);
            if (document?.Root is null)
            {
                continue;
            }

            foreach (var element in document.Root.Descendants("PackageReference"))
            {
                var include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                injected.Add(
                    new InjectedPackageReference(
                        include.Trim(),
                        ReadVersion(element),
                        ReadPrivateAssets(element),
                        file
                    )
                );
            }
        }

        return injected;
    }

    /// <summary>File names MSBuild imports into every governed project, nearest-first per project.</summary>
    private static readonly string[] BuildImportFileNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
    ];

    /// <summary>
    /// One <c>PackageReference Include</c> item read from a governing import file.
    /// </summary>
    /// <param name="Package">The package id, verbatim.</param>
    /// <param name="Version">The item's <c>Version</c> metadata, or null when the pin supplies it.</param>
    /// <param name="PrivateAssets">
    /// The item's unconditional <c>PrivateAssets</c>, or null when absent or conditioned — the same
    /// coverage judgment the central-pin reader makes.
    /// </param>
    /// <param name="DeclaringFile">Absolute path of the import file the item was read from.</param>
    internal readonly record struct InjectedPackageReference(
        string Package,
        string? Version,
        string? PrivateAssets,
        string DeclaringFile
    );

    /// <summary>
    /// The nearest file of a given name at or above a directory, stopping at the repository root —
    /// the same boundary the <c>Directory.Build.props</c> walk keeps.
    /// </summary>
    private static string? WalkUpForFile(string? startDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (MsBuildProps.IsRepositoryRoot(directory.FullName))
            {
                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

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
    private static string? ResolvePropsPath(
        string? basePath,
        StringComparer pathComparer
    )
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        // A repository can point central management at a file of its own choosing, and MSBuild then
        // imports that instead of the nearest conventional one. Ignoring the redirect judged the
        // project against pins it never receives and told the reader to edit a file MSBuild does not
        // read for it. When the redirect cannot be resolved no file is claimed at all: saying
        // nothing is better than measuring a project against the wrong file.
        if (MsBuildProps.TryReadRedirectedPropsPath(basePath, pathComparer, out var redirected))
        {
            return redirected;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(basePath));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, PropsFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (MsBuildProps.IsRepositoryRoot(directory.FullName))
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
    private static (XDocument Document, string Path)? ReadNearestBuildProps(
        string propsPath,
        string? basePath
    )
    {
        // From the scan root upwards first, because that is where MSBuild starts and the nearest
        // file wins: with /repo/Directory.Build.props setting the property one way and
        // /repo/src/Directory.Build.props setting it the other, a project under src gets the
        // nearer answer. Checking only the two endpoints picked the wrong one.
        var fromScanRoot = MsBuildProps.WalkUpForBuildProps(basePath);
        if (fromScanRoot is not null)
        {
            return fromScanRoot;
        }

        // Then beside the props file, which may sit above the scan root and above the boundary the
        // walk stops at.
        return MsBuildProps.WalkUpForBuildProps(
            Path.GetDirectoryName(Path.GetFullPath(propsPath)),
            single: true
        );
    }
}
