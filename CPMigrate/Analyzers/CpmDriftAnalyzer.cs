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

    private const string RedirectProperty = "DirectoryPackagesPropsPath";

    private const string TransitivePinningProperty = "CentralPackageTransitivePinningEnabled";

    /// <summary>Unit separator: it cannot occur in a path, so composite keys stay unambiguous.</summary>
    private const string KeySeparator = "\u001F";

    /// <summary>
    /// Chooses the path comparer for the filesystem containing a scan root.
    ///
    /// <para>
    /// The operating system is not a sufficient proxy for filesystem semantics: macOS can use a
    /// case-sensitive APFS volume, and Windows supports case-sensitive directories. Probing beside
    /// the scan root keeps two distinct contexts such as <c>tools/</c> and <c>Tools/</c> distinct
    /// wherever the filesystem does. If the probe cannot run, ordinal comparison is the
    /// conservative fallback: it may inspect one case-insensitive path twice, but it cannot merge
    /// two contexts and silently lose one of their pins.
    /// </para>
    /// </summary>
    internal static StringComparer PathComparerFor(string? scanRoot)
    {
        if (string.IsNullOrWhiteSpace(scanRoot))
        {
            return StringComparer.Ordinal;
        }

        var root = Path.GetFullPath(scanRoot);
        if (!Directory.Exists(root))
        {
            return StringComparer.Ordinal;
        }

        var probeDirectory = Path.Combine(root, $".cpmigrate-case-probe-{Guid.NewGuid():N}");
        var probeFile = Path.Combine(probeDirectory, "probe");
        var differentlyCasedProbeFile = Path.Combine(probeDirectory, "PROBE");

        try
        {
            Directory.CreateDirectory(probeDirectory);
            File.WriteAllText(probeFile, string.Empty);

            return File.Exists(differentlyCasedProbeFile)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException
        )
        {
            return StringComparer.Ordinal;
        }
        finally
        {
            try
            {
                if (Directory.Exists(probeDirectory))
                {
                    Directory.Delete(probeDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or System.Security.SecurityException
            )
            {
                // A failed cleanup must not make an otherwise valid scan fail.
            }
        }
    }

    /// <inheritdoc />
    public string Name => "Central Package Management Drift";

    /// <inheritdoc />
    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);
        return Analyze(packageInfo, PathComparerFor(packageInfo.BasePath));
    }

    /// <summary>
    /// Analyzes a package scan with an explicit path comparer. The overload is internal so tests can
    /// exercise case-distinct contexts even on a case-insensitive host; production scans always use
    /// <see cref="PathComparerFor(string?)"/> for their scan root.
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
                var props = ReadProps(propsPath)!;
                var (central, importsResolved) = ReadCentralVersions(
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
        enablement = ReadPropertyThroughImports(
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
                && ReadPropertyThroughImports(
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
        var effective = ReadPropertyThroughImports(
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
        var project = ReadProps(projectPath);

        return project is null
            ? null
            : ReadPropertyThroughImports(
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
                                + $"Move it to {propsFile}.",
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
                    Fixable: false,
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
        bool ImportsResolved
    ) ReadCentralVersions(
        XDocument props,
        string propsPath,
        StringComparer pathComparer
    )
    {
        var versions = new Dictionary<string, CentralEntry>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(pathComparer);
        var resolved = CollectCentralVersions(props, propsPath, versions, visited, pathComparer);

        return (versions, resolved);
    }

    private static bool CollectCentralVersions(
        XDocument document,
        string documentPath,
        Dictionary<string, CentralEntry> versions,
        HashSet<string> visited,
        StringComparer pathComparer
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
                allResolved &= FollowImport(
                    element,
                    documentPath,
                    versions,
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
            if (!string.IsNullOrWhiteSpace(packageName))
            {
                versions[packageName] = new CentralEntry(
                    ReadVersion(element),
                    isGlobal,
                    documentPath
                );
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
        HashSet<string> visited,
        StringComparer pathComparer
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

        return CollectCentralVersions(imported, importedPath, versions, visited, pathComparer);
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
    private readonly record struct CentralEntry(string? Version, bool IsGlobal, string SourcePath);

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
        IEnumerable<string>? projectPaths = null,
        StringComparer? pathComparer = null
    )
    {
        var effective = new List<CentralPin>();
        pathComparer ??= PathComparerFor(basePath);

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
        var props = ReadProps(propsPath);
        if (
            props is null
            || (!IsCpmEnabled(props, propsPath, propertyRoot, out _, pathComparer) && !optedIn)
        )
        {
            return;
        }

        var (central, _) = ReadCentralVersions(props, propsPath, pathComparer);

        foreach (
            var entry in central.Where(entry => !string.IsNullOrWhiteSpace(entry.Value.Version))
        )
        {
            // Every distinct pin, not one per package. Collapsing by package let a root file's exact
            // pin hide a nested file's floating one — the nested project's dependency would then
            // pass as reproducible when it is not, which is the failure this whole rule exists to
            // catch.
            // Attributed to the file that declared it, not the one that imported it: a pin
            // reported against the importing file names the wrong place to edit, and the same
            // inherited pin would be reported once per file that imports it.
            var pin = new CentralPin(
                entry.Key,
                entry.Value.Version!,
                // Relative to the *scan root*, not the project directory the properties were
                // resolved from — otherwise a nested file reads as '../Directory.Packages.props'
                // and two different nested files collapse onto one name.
                DescribePropsPath(entry.Value.SourcePath, scanRoot)
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
        if (TryReadRedirectedPropsPath(basePath, pathComparer, out var redirected))
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
    /// The file <c>DirectoryPackagesPropsPath</c> names, when the nearest
    /// <c>Directory.Build.props</c> sets one.
    ///
    /// <para>
    /// Only <c>$(MSBuildThisFileDirectory)</c> is substituted. Anchoring the path to the file that
    /// declares it is how this is written in practice, and any other property left unevaluated means
    /// the real answer is unknown — a guess here picks the wrong file and every finding drawn from it
    /// is wrong.
    /// </para>
    /// </summary>
    /// <param name="startDirectory">Directory of the project whose props file is being resolved.</param>
    /// <param name="resolved">The redirected file, or null when it could not be resolved.</param>
    /// <returns>True when a redirect was declared at all, resolved or not.</returns>
    private static bool TryReadRedirectedPropsPath(
        string startDirectory,
        StringComparer pathComparer,
        out string? resolved
    )
    {
        resolved = null;

        if (WalkUpForBuildProps(startDirectory) is not { } buildProps)
        {
            return false;
        }

        var (document, buildPropsPath) = buildProps;

        // Followed through imports, because delegating shared settings to an imported fragment is
        // ordinary and MSBuild observes the redirect wherever it is written. Reading only the outer
        // document fell back to the conventional file and judged the project against pins it never
        // receives.
        if (
            ReadPropertyThroughImports(
                document,
                buildPropsPath,
                RedirectProperty,
                new HashSet<string>(pathComparer),
                pathComparer
            )
            is not { } declaration
        )
        {
            return false;
        }

        // The directory of the file that *declared* it, not the outer one: a redirect anchored with
        // $(MSBuildThisFileDirectory) means its own file, and so does a bare relative path.
        var (declared, directory) = declaration;

        if (string.IsNullOrWhiteSpace(declared))
        {
            return false;
        }

        var expanded = declared
            .Replace(
                "$(MSBuildThisFileDirectory)",
                directory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return true;
        }

        var candidate = Path.GetFullPath(
            Path.IsPathRooted(expanded) ? expanded : Path.Combine(directory, expanded)
        );

        if (File.Exists(candidate))
        {
            resolved = candidate;
        }

        return true;
    }

    /// <summary>
    /// The last <c>DirectoryPackagesPropsPath</c> assignment in a document, following unconditional
    /// imports in evaluation order, paired with the directory of the file that declared it.
    ///
    /// <para>
    /// An import that cannot be resolved by reading XML — conditioned, globbed, or built from
    /// properties — is skipped rather than followed, the same trade made for the enablement
    /// property: acting on an import that may not apply is worse than not reading it.
    /// </para>
    /// </summary>
    private static (string Value, string Directory)? ReadPropertyThroughImports(
        XDocument document,
        string documentPath,
        string propertyName,
        HashSet<string> visited,
        StringComparer pathComparer
    )
    {
        var fullPath = Path.GetFullPath(documentPath);
        if (!visited.Add(fullPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
        {
            return null;
        }

        (string Value, string Directory)? resolved = null;

        // Document order, last assignment wins — how MSBuild evaluates a file. Reading the local
        // value first instead would let an import turn a property on but never off.
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                // Paired with the directory of the file that *declared* it, because a path-valued
                // property is anchored to its own file, not to whichever one imported it.
                resolved = (element.Value.Trim(), directory);
                continue;
            }

            if (!element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryResolveImportPath(element, directory, out var importedPath))
            {
                continue;
            }

            var imported = ReadProps(importedPath);
            if (imported is null)
            {
                continue;
            }

            if (
                ReadPropertyThroughImports(
                    imported,
                    importedPath,
                    propertyName,
                    visited,
                    pathComparer
                ) is
                { } inherited
            )
            {
                resolved = inherited;
            }
        }

        return resolved;
    }

    /// <summary>
    /// The file an <c>Import</c> names, when reading XML alone can say what it is.
    ///
    /// <para>
    /// <c>$(MSBuildThisFileDirectory)</c> is substituted rather than rejected: it is statically
    /// known, and anchoring an import with it is the ordinary way to write a portable one, so
    /// treating it as unresolvable left the commonest form unread. Any other property, or a glob,
    /// genuinely cannot be resolved here.
    /// </para>
    ///
    /// <para>
    /// A condition on the import <em>or on any element enclosing it</em> makes it unresolved. An
    /// <c>Import</c> inside a conditioned <c>ImportGroup</c> is as conditional as one carrying the
    /// attribute itself, and acting on a group that may not apply would swap in a pin set MSBuild
    /// never uses.
    /// </para>
    /// </summary>
    private static bool TryResolveImportPath(
        XElement element,
        string directory,
        out string resolved
    )
    {
        resolved = string.Empty;

        for (XElement? enclosing = element; enclosing is not null; enclosing = enclosing.Parent)
        {
            if (!string.IsNullOrWhiteSpace(enclosing.Attribute("Condition")?.Value))
            {
                return false;
            }
        }

        var relative = element.Attribute("Project")?.Value;
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('*'))
        {
            return false;
        }

        var expanded = relative.Replace(
            "$(MSBuildThisFileDirectory)",
            directory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );

        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        resolved = Path.GetFullPath(
            Path.Combine(directory, expanded.Replace('\\', Path.DirectorySeparatorChar))
        );

        return true;
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
    private static (XDocument Document, string Path)? ReadNearestBuildProps(
        string propsPath,
        string? basePath
    )
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
    private static (XDocument Document, string Path)? WalkUpForBuildProps(
        string? startDirectory,
        bool single = false
    )
    {
        const string BuildPropsFileName = "Directory.Build.props";

        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            // The path travels with the document because reading a property through imports needs
            // to know which file it came from, both to resolve relative imports and to anchor a
            // path-valued property.
            var candidate = Path.Combine(directory.FullName, BuildPropsFileName);
            var found = ReadProps(candidate);
            if (found is not null)
            {
                return (found, candidate);
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
