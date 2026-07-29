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

    /// <inheritdoc />
    public string Name => "Central Package Management Drift";

    /// <inheritdoc />
    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);

        var issues = new List<AnalysisIssue>();
        var propsPath = ResolvePropsPath(packageInfo.BasePath);

        if (propsPath is null)
        {
            // Not a centrally-managed solution: there is nothing to drift from, and reporting
            // "no props file" as a finding would fire on every pre-migration repository.
            return new AnalyzerResult(Name, issues);
        }

        var props = ReadProps(propsPath);
        if (props is null)
        {
            issues.Add(
                new AnalysisIssue(
                    PropsFileName,
                    $"{PropsFileName} exists but could not be parsed as XML, so central versions "
                        + "cannot be verified.",
                    Array.Empty<string>(),
                    AnalysisIssueCode.CpmNotEnabled,
                    AnalysisSeverity.High
                )
            );

            return new AnalyzerResult(Name, issues);
        }

        AddCpmNotEnabledIssue(issues, props, packageInfo.BasePath);

        var central = ReadCentralVersions(props);

        // A GlobalPackageReference applies to every project by definition, so it is never orphaned —
        // seeding it here keeps the orphan check from reporting all of them.
        var referenced = new HashSet<string>(
            central.Where(entry => entry.Value.IsGlobal).Select(entry => entry.Key),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var projectPath in packageInfo.GetProjectsScanned())
        {
            InspectProject(issues, packageInfo, projectPath, central, referenced);
        }

        AddOrphanedVersionIssues(issues, central, referenced);

        return new AnalyzerResult(Name, issues);
    }

    /// <summary>
    /// Reports a props file that exists without central management actually switched on, which
    /// leaves every <c>PackageVersion</c> entry inert — the file looks authoritative and does
    /// nothing.
    /// </summary>
    private static void AddCpmNotEnabledIssue(
        List<AnalysisIssue> issues,
        XDocument props,
        string? basePath
    )
    {
        var enabled = ReadCpmEnablement(props);

        if (string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (enabled is null)
        {
            // MSBuild resolves the property through imports, and Directory.Build.props is the other
            // conventional home for it. Absence from both is still not proof — a custom import could
            // set it — but reporting on the props file alone produces a High-severity false positive
            // on repositories that are perfectly well configured.
            var buildProps = basePath is null
                ? null
                : ReadProps(Path.Combine(basePath, "Directory.Build.props"));

            if (buildProps is not null && ReadCpmEnablement(buildProps) is { } inherited)
            {
                if (string.Equals(inherited, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                enabled = inherited;
            }
        }

        issues.Add(
            new AnalysisIssue(
                PropsFileName,
                enabled is null
                    ? $"{PropsFileName} exists but does not set ManagePackageVersionsCentrally, so "
                        + "its PackageVersion entries are ignored."
                    : $"{PropsFileName} sets ManagePackageVersionsCentrally to '{enabled}', so its "
                        + "PackageVersion entries are ignored.",
                Array.Empty<string>(),
                AnalysisIssueCode.CpmNotEnabled,
                AnalysisSeverity.High
            )
        );
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
        HashSet<string> referenced
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

            var inlineVersion = ReadVersion(element);
            if (inlineVersion is not null)
            {
                issues.Add(
                    new AnalysisIssue(
                        packageName,
                        central.TryGetValue(packageName, out var centralEntry)
                        && centralEntry.Version is not null
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

            if (!central.ContainsKey(packageName))
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
        HashSet<string> referenced
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
                    $"Pinned at {entry.Version ?? "an unspecified version"} in {PropsFileName} but "
                        + "referenced by no project. Remove it, or the pin outlives what it was for.",
                    Array.Empty<string>(),
                    AnalysisIssueCode.OrphanedPackageVersion,
                    AnalysisSeverity.Low,
                    Fixable: false
                )
            );
        }
    }

    /// <summary>
    /// Central versions by package ID. <c>GlobalPackageReference</c> counts: it supplies a version
    /// centrally too, so a project referencing such a package is not missing anything.
    /// </summary>
    private static Dictionary<string, CentralEntry> ReadCentralVersions(XDocument props)
    {
        var versions = new Dictionary<string, CentralEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in props.Descendants())
        {
            var isPackageVersion = element.Name.LocalName.Equals(
                PackageVersionItem,
                StringComparison.OrdinalIgnoreCase
            );
            var isGlobal = element.Name.LocalName.Equals(
                GlobalPackageReferenceItem,
                StringComparison.OrdinalIgnoreCase
            );

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

        return versions;
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
        var attribute = element.Attribute("Version")?.Value;
        if (!string.IsNullOrWhiteSpace(attribute))
        {
            return attribute.Trim();
        }

        var child = element
            .Elements()
            .FirstOrDefault(e =>
                e.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase)
            )
            ?.Value;

        return string.IsNullOrWhiteSpace(child) ? null : child.Trim();
    }

    private static string? ResolvePropsPath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        var candidate = Path.Combine(basePath, PropsFileName);
        return File.Exists(candidate) ? candidate : null;
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
