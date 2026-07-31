using CPMigrate.Models;
using NuGet.Versioning;

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
    /// Classifies a version specification, or null when only one release can satisfy it.
    ///
    /// <para>
    /// Decided by <see cref="VersionRange"/> rather than by reading the string, because the question
    /// — can this resolve to more than one version? — is exactly what NuGet's own parser answers,
    /// and the string forms that mean "exactly one" are not obvious. <c>[1.0.0]</c> is the familiar
    /// one; <c>[1.0.0,1.0.0]</c> is a range whose bounds coincide, so it is every bit as
    /// reproducible, and reporting it would flag a deterministic pin as a defect.
    /// </para>
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

        if (!VersionRange.TryParse(trimmed, out var range))
        {
            // Not something NuGet can read. Whatever is wrong with it, this rule is not the one to
            // say so — restore will, and loudly.
            return null;
        }

        if (range.IsFloating)
        {
            return WildcardKind;
        }

        if (!IsBracketed(trimmed))
        {
            // A bare "13.0.1" parses as [13.0.1,), because that is NuGet's documented meaning: a
            // minimum. It is not what anyone writing it means, and resolution picks that exact
            // version whenever the feed has it — so reporting it would flag every ordinary pin in
            // every project, which is how a rule becomes noise and then gets switched off.
            return null;
        }

        return PinsOneVersion(range) ? null : RangeKind;
    }

    /// <summary>
    /// Whether the specification was written as an explicit interval, which is the only form that
    /// deliberately admits more than one version.
    /// </summary>
    private static bool IsBracketed(string version)
    {
        return version.Length >= 2 && version[0] is '[' or '(' && version[^1] is ']' or ')';
    }

    /// <summary>
    /// Whether a range admits exactly one version: both bounds present, both inclusive, and equal.
    /// </summary>
    private static bool PinsOneVersion(VersionRange range)
    {
        if (!range.HasLowerAndUpperBounds || !range.IsMinInclusive || !range.IsMaxInclusive)
        {
            return false;
        }

        return range.MinVersion.Equals(range.MaxVersion);
    }

    /// <summary>
    /// Central pins, read through <see cref="CpmDriftAnalyzer.ReadEffectiveCentralVersions"/> rather
    /// than a second parser of this file's own.
    ///
    /// <para>
    /// A rule with its own reader silently misses whichever declaration forms it did not think of —
    /// an imported props file, the <c>Update</c> form, child-element <c>&lt;Version&gt;</c> metadata
    /// (which this repository's own props file uses) — and reports the solution clean, which looks
    /// exactly like a solution with nothing wrong. Sharing the parser also means an inert
    /// <c>PackageVersion</c>, in a props file with central management switched off, is not reported
    /// as a floating version NuGet would never consult.
    /// </para>
    /// </summary>
    private static IEnumerable<Declaration> ReadCentralPins(string? basePath)
    {
        return CpmDriftAnalyzer
            .ReadEffectiveCentralVersions(basePath)
            .Select(entry => new Declaration(
                entry.Key,
                entry.Value,
                // The pin lives in the props file, not in any one project, so naming projects here
                // would point at the wrong file to edit.
                Project: string.Empty
            ))
            .ToList();
    }
}
