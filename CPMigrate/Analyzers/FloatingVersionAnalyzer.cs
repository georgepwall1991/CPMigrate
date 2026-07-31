using System.Xml;
using System.Xml.Linq;
using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Reports package versions that are not pinned to one release.
///
/// <para>
/// A wildcard (<c>4.*</c>) or an open range (<c>[4.0.0,)</c>) means the version is chosen by
/// whatever the feed happens to hold at restore time, so the same commit builds against different
/// code tomorrow. Nothing surfaces that: a wildcard resolving to a new major is a perfectly
/// successful restore, and a green CI run against one version says nothing about the one that gets
/// built next. It is also why a bisect can fail to reproduce — the tree is not the whole input.
/// </para>
///
/// <para>
/// Read from what the files <em>declare</em>, never from the resolved graph. Resolution has already
/// turned <c>4.*</c> into a concrete version, so a rule reading <c>dotnet package list</c> could
/// never fire — the shape that left three earlier rules silent for many releases. Central pins are
/// read straight out of <c>Directory.Packages.props</c>, because after a migration that is where
/// every version lives and a rule confined to project files would go quiet on exactly the solutions
/// this tool produces.
/// </para>
/// </summary>
public class FloatingVersionAnalyzer : IAnalyzer
{
    private const string WildcardKind = "wildcard";
    private const string RangeKind = "range";

    /// <summary>One declaration that might be floating, and where it was written.</summary>
    /// <param name="PackageName">Package the specification applies to.</param>
    /// <param name="Version">The specification exactly as written.</param>
    /// <param name="Project">
    /// Project id, or empty for a central pin — which lives in the props file rather than in any one
    /// project, so naming projects would point at the wrong file to edit.
    /// </param>
    private sealed record Declaration(string PackageName, string Version, string Project);

    /// <inheritdoc />
    public string Name => "Floating Versions";

    /// <inheritdoc />
    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);

        var declarations = packageInfo
            .GetDeclaredReferences()
            .Where(reference => !reference.IsTransitive)
            .Select(reference => new Declaration(
                reference.PackageName,
                reference.Version,
                packageInfo.ProjectId(reference.ProjectPath)
            ))
            .Concat(ReadCentralPins(packageInfo.BasePath))
            .Where(declaration => DescribeKind(declaration.Version) is not null)
            .ToList();

        // Grouped by package *and* specification: one decision produces one finding however many
        // projects share it, while two different specifications stay two findings, because they are
        // two strings someone has to go and change.
        var issues = declarations
            .GroupBy(declaration =>
                (
                    Package: declaration.PackageName.ToLowerInvariant(),
                    Specification: declaration.Version.Trim().ToLowerInvariant()
                )
            )
            .Select(group => Report(group.ToList()))
            .ToList();

        return new AnalyzerResult(Name, issues);
    }

    private static AnalysisIssue Report(IReadOnlyList<Declaration> group)
    {
        var packageName = group[0].PackageName;
        var specification = group[0].Version.Trim();
        var kind = DescribeKind(specification)!;

        var projects = group
            .Select(declaration => declaration.Project)
            .Where(project => !string.IsNullOrEmpty(project))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToList();

        var explanation =
            kind == WildcardKind
                ? "resolves to whatever the feed holds at restore time"
                : "floats within a range, so restore can pick a different version tomorrow";

        return new AnalysisIssue(
            packageName,
            $"Version '{specification}' {explanation}. Pin an exact version so the same commit "
                + "restores the same package.",
            projects,
            AnalysisIssueCode.FloatingVersion,
            AnalysisSeverity.Moderate,
            // Deliberately not fixable: choosing the release a float should become needs the feed,
            // which this pass does not query. Claiming otherwise would have --fix report "no
            // changes were needed" over a finding it never touched.
            Fixable: false,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["versionSpecification"] = specification,
                ["kind"] = kind,
            }
        );
    }

    /// <summary>
    /// Classifies a version specification, or null when it pins exactly one release.
    ///
    /// <c>[1.0.0]</c> is the most exact form NuGet has — a bracketed single version locks the
    /// package to it — so it must not be read as a range just because it has brackets.
    /// </summary>
    private static string? DescribeKind(string? version)
    {
        var trimmed = version?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            // No version at all is either central package management or a restore that will fail,
            // and MissingPackageVersion says which. Reporting it here would double-count it.
            return null;
        }

        if (trimmed.Contains('*', StringComparison.Ordinal))
        {
            return WildcardKind;
        }

        if (!IsBracketed(trimmed))
        {
            return null;
        }

        return trimmed[1..^1].Contains(',', StringComparison.Ordinal) ? RangeKind : null;
    }

    private static bool IsBracketed(string version)
    {
        return version.Length >= 2 && version[0] is '[' or '(' && version[^1] is ']' or ')';
    }

    /// <summary>
    /// Central pins, read from the props file rather than from the scan. Projects under central
    /// package management carry no version of their own, so this is the only place the
    /// specification exists.
    /// </summary>
    private static IEnumerable<Declaration> ReadCentralPins(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return [];
        }

        var propsPath = Path.Combine(basePath, CpmDriftAnalyzer.PropsFileName);
        if (!File.Exists(propsPath))
        {
            return [];
        }

        XDocument props;
        try
        {
            props = XDocument.Load(propsPath);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // An unreadable props file is CpmDriftAnalyzer's finding to report; duplicating it here
            // would say the same thing twice under a rule that is not about parsing.
            return [];
        }

        return props
            .Descendants()
            .Where(element =>
                element.Name.LocalName is "PackageVersion" or "GlobalPackageReference"
            )
            .Select(element => new Declaration(
                (string?)element.Attribute("Include") ?? string.Empty,
                (string?)element.Attribute("Version") ?? string.Empty,
                Project: string.Empty
            ))
            .Where(declaration => !string.IsNullOrWhiteSpace(declaration.PackageName))
            .ToList();
    }
}
