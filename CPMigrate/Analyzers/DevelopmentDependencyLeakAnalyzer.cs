using CPMigrate.Models;
using NuGet.Versioning;

namespace CPMigrate.Analyzers;

/// <summary>
/// Reports development-only packages — analyzers, test SDKs, coverage tools, source generators —
/// referenced without <c>PrivateAssets</c> scoping, so they flow transitively into every consumer's
/// dependency graph.
///
/// <para>
/// A package that only contributes at build time should say so. Without <c>PrivateAssets="all"</c>,
/// a library that references an analyzer or a coverage collector hands that package to everyone who
/// references the library: it lands in the produced nuspec's dependency list, in downstream lock
/// files, and in their builds. NuGet does not warn about it — the reference resolves fine — so the
/// leak is silent until someone reads their own dependency list and finds a test framework in it.
/// </para>
///
/// <para>
/// Dev-only is decided two ways. The convention set — a package id ending in
/// <c>.Analyzer</c>/<c>.Analyzers</c>, the test and coverage packages named here — needs no data
/// and never goes stale. And when the analysis pipeline attached a nuspec scan, a package whose
/// nuspec self-declares <c>developmentDependency="true"</c> counts too: a dev-only tool under an
/// ordinary name escapes every suffix the convention can guess, but it has already said what it
/// is. The nuspec answer is version-precise — a project pinned to a non-dev version is not flagged
/// on another project's nuspec — and absent when packages are not restored, where only the
/// convention applies.
/// </para>
///
/// <para>
/// Coverage is read only from unconditional declarations: <c>PrivateAssets</c> set for one
/// configuration still lets the package flow on every other, and a <c>PrivateAssets</c> value other
/// than <c>all</c> scopes only part of the package. Central scoping counts too — a
/// <c>PackageVersion</c> or <c>GlobalPackageReference</c> carrying <c>PrivateAssets="all"</c> is the
/// intended way to apply it centrally under CPM. A <c>GlobalPackageReference</c> for a dev-only
/// package that carries no such scoping is itself the leak: it injects the reference into every
/// governed project, so the finding names the props file rather than any one project.
/// </para>
/// </summary>
public class DevelopmentDependencyLeakAnalyzer : IAnalyzer
{
    /// <summary>
    /// Package ids that are definitionally development-only: they ship test infrastructure,
    /// coverage collection, or source linking and contribute nothing a consumer could need.
    /// Compared case-insensitively because NuGet package ids are.
    /// </summary>
    private static readonly HashSet<string> DevelopmentOnlyPackages = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Test SDKs and adapters — needed to run the tests, never to consume the library.
        "Microsoft.NET.Test.Sdk",
        "Microsoft.TestPlatform.TestHost",
        "Microsoft.CodeCoverage",
        "MSTest.TestAdapter",
        "MSTest.TestFramework",
        "NUnit3TestAdapter",
        "xunit.runner.visualstudio",
        "xunit.runner.console",
        // Coverage collectors.
        "coverlet.collector",
        "coverlet.msbuild",
        // Roslyn analysis infrastructure — analyzer packages and analyzer-test harnesses.
        "Microsoft.CodeAnalysis.NetAnalyzers",
        "Microsoft.CodeAnalysis.Analyzers",
        "Microsoft.CodeAnalysis.BannedApiAnalyzers",
        "Microsoft.CodeAnalysis.PublicApiAnalyzers",
        "Microsoft.CodeAnalysis.CSharp.CodeFix.Testing",
        "Microsoft.CodeAnalysis.Testing.Verifiers.XUnit",
    };

    /// <summary>
    /// Id prefixes whose packages are development-only as a family: SourceLink embeds repository
    /// metadata at pack time, the TestPlatform family is runner infrastructure, and the
    /// analyzer-testing packages exist to host analyzers under test.
    /// </summary>
    private static readonly string[] DevelopmentOnlyPrefixes =
    [
        "Microsoft.SourceLink.",
        "Microsoft.TestPlatform.",
        "Microsoft.Testing.Extensions.",
        "Microsoft.CodeAnalysis.Testing.",
        // The SonarSource analyzers carry the language in the id — .CSharp, .VisualBasic,
        // .Styling — rather than ending in .Analyzers.
        "SonarAnalyzer.",
    ];

    /// <inheritdoc />
    public string Name => "Development Dependency Leaks";

    /// <inheritdoc />
    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);

        var pins = CpmDriftAnalyzer.ReadEffectiveCentralVersions(
            packageInfo.BasePath,
            packageInfo.GetProjectsScanned()
        );

        var issues = new List<AnalysisIssue>();
        var scan = packageInfo.DevelopmentDependencies;

        // A GlobalPackageReference is itself a declaration: it injects the package into every
        // governed project, so when it is dev-only and unscoped the leak lives in the props file.
        foreach (var pin in pins)
        {
            if (
                pin.IsGlobal
                && IsDevelopmentOnlyPin(pin.Package, pin.Version, scan)
                && !CoversAll(pin.PrivateAssets)
            )
            {
                issues.Add(ReportGlobal(pin));
            }
        }

        // A centrally scoped pin covers every project the file governs. Contexts merge across
        // props files, so a package covered under any governing file is treated as covered — the
        // direction that avoids accusing a repository whose scoping is correct.
        var centralCoverage = pins
            .Where(pin => CoversAll(pin.PrivateAssets))
            .Select(pin => pin.Package)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // PackageReference items in governing import files inject into every project beneath them
        // — Directory.Build.props/targets, and Directory.Packages.props — but never appear in any
        // project's declaration list. An unscoped dev-only Include there is the same leak a
        // GlobalPackageReference makes, so it is reported against the file, not a project.
        var injected = CpmDriftAnalyzer.ReadBuildImportPackageReferences(
            packageInfo.GetProjectsScanned(),
            CpmDriftAnalyzer.PathComparerFor(packageInfo.BasePath)
        );

        foreach (
            var importGroup in injected.GroupBy(
                reference => (reference.DeclaringFile, reference.Package),
                InjectedGroupComparer.Instance
            )
        )
        {
            if (
                centralCoverage.Contains(importGroup.Key.Package)
                || importGroup.All(reference => CoversAll(reference.PrivateAssets))
                || !IsDevelopmentOnlyInjected(importGroup.Key.Package, importGroup, scan, pins)
            )
            {
                continue;
            }

            issues.Add(ReportInjected(importGroup.Key.DeclaringFile, importGroup.Key.Package, packageInfo.BasePath));
        }

        // DeclaredReferences only: the resolved list never carries PrivateAssets — resolution has
        // already applied it — so falling back to it would read every dev-only package as
        // uncovered. A null list means the declarations could not be read, and "could not look"
        // must not report the same findings as "looked, and they were missing".
        if (packageInfo.DeclaredReferences is null)
        {
            return new AnalyzerResult(Name, issues);
        }

        foreach (
            var packageGroup in packageInfo
                .DeclaredReferences.Where(reference => !reference.IsTransitive)
                .GroupBy(
                    reference => reference.PackageName,
                    StringComparer.OrdinalIgnoreCase
                )
        )
        {
            if (centralCoverage.Contains(packageGroup.Key))
            {
                continue;
            }

            // Per project, and per the version that project resolves: the group must contain a real
            // declaration (a metadata-only Update alone does not create the reference), no member
            // may scope every asset private, and dev-only must hold for *this* project — the
            // convention answers by id, the nuspec scan by the resolved version.
            var uncovered = packageGroup
                .GroupBy(reference => reference.ProjectPath, StringComparer.Ordinal)
                .Where(projectGroup => projectGroup.Any(reference => !reference.IsMetadataOnlyUpdate))
                .Where(projectGroup => !projectGroup.Any(HasCoveringPrivateAssets))
                .Where(projectGroup =>
                    IsDevelopmentOnly(packageGroup.Key, projectGroup.Key, scan)
                )
                .Select(projectGroup => packageInfo.ProjectId(projectGroup.Key))
                .OrderBy(project => project, StringComparer.Ordinal)
                .ToList();

            if (uncovered.Count > 0)
            {
                issues.Add(Report(packageGroup.Key, uncovered));
            }
        }

        return new AnalyzerResult(Name, issues);
    }

    /// <summary>
    /// One package leaked through one or more project references. The fix is mechanical —
    /// <c>PrivateAssets="all"</c> on each reference — so the finding is fixable.
    /// </summary>
    private static AnalysisIssue Report(string packageName, IReadOnlyList<string> projectIds)
    {
        return new AnalysisIssue(
            packageName,
            $"{packageName} is a development-only package but the reference does not set "
                + "PrivateAssets=\"all\", so it flows transitively to every project or package that "
                + "consumes this one. Scope it privately, or set the assets centrally on the "
                + "PackageVersion entry.",
            projectIds,
            AnalysisIssueCode.DevelopmentDependencyLeak,
            AnalysisSeverity.Low,
            Fixable: true,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suggestedPrivateAssets"] = "all",
            }
        );
    }

    /// <summary>
    /// The leak a <c>GlobalPackageReference</c> makes: every governed project gets the reference,
    /// so the file that declares it is the place to fix it.
    /// </summary>
    private static AnalysisIssue ReportGlobal(CpmDriftAnalyzer.CentralPin pin)
    {
        return new AnalysisIssue(
            pin.Package,
            $"{pin.Package} is a development-only package injected into every project by "
                + $"a GlobalPackageReference in {pin.PropsFile} without PrivateAssets=\"all\", so it "
                + "flows to every consumer of every project. Set the assets on the global entry.",
            [],
            AnalysisIssueCode.DevelopmentDependencyLeak,
            AnalysisSeverity.Low,
            Fixable: true,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suggestedPrivateAssets"] = "all",
                ["propsFile"] = pin.PropsFile,
            }
        );
    }

    /// <summary>
    /// Whether an injected reference's package is development-only: the convention answers by id;
    /// the nuspec scan answers by the item's own version, or by the central pin's when the item
    /// carries none — the same precedence the resolved graph would give it.
    /// </summary>
    private static bool IsDevelopmentOnlyInjected(
        string packageName,
        IEnumerable<CpmDriftAnalyzer.InjectedPackageReference> items,
        DevelopmentDependencyScanResult? scan,
        IReadOnlyList<CpmDriftAnalyzer.CentralPin> pins
    )
    {
        if (IsDevelopmentOnlyByConvention(packageName) || scan is null)
        {
            return IsDevelopmentOnlyByConvention(packageName);
        }

        var candidates = items
            .Select(item => item.Version)
            .Concat(
                pins.Where(pin =>
                        string.Equals(pin.Package, packageName, StringComparison.OrdinalIgnoreCase)
                    )
                    .Select(pin => (string?)pin.Version)
            );

        return candidates.Any(version =>
            version is not null
            && NuGetVersion.TryParse(version, out var parsed)
            && scan.Versions.Contains(
                new PackageVersionKey(packageName, parsed.ToNormalizedString().ToLowerInvariant())
            )
        );
    }

    /// <summary>
    /// The leak an import-file <c>PackageReference</c> makes: every governed project gets the
    /// reference, so the file that declares it is the place to fix it.
    /// </summary>
    private static AnalysisIssue ReportInjected(
        string declaringFile,
        string packageName,
        string? basePath
    )
    {
        var file = DescribeRelativeTo(declaringFile, basePath);
        return new AnalysisIssue(
            packageName,
            $"{packageName} is a development-only package injected into every governed project by "
                + $"a PackageReference in {file} without PrivateAssets=\"all\", so it flows to "
                + "every consumer of every project. Set the assets on the reference in the import "
                + "file, or centrally on the PackageVersion entry.",
            [],
            AnalysisIssueCode.DevelopmentDependencyLeak,
            AnalysisSeverity.Low,
            Fixable: true,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suggestedPrivateAssets"] = "all",
                ["propsFile"] = file,
            }
        );
    }

    /// <summary>
    /// A path relative to the scan root when it sits beneath it, verbatim otherwise — the same
    /// description the drift analyzer gives props files so a finding names the file to edit.
    /// </summary>
    private static string DescribeRelativeTo(string path, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return path;
        }

        var relative = Path.GetRelativePath(
            Path.GetFullPath(basePath),
            Path.GetFullPath(path)
        );

        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }

    /// <summary>Groups injected items by (declaring file, package), both compared ordinally.</summary>
    private sealed class InjectedGroupComparer
        : IEqualityComparer<(string DeclaringFile, string Package)>
    {
        public static readonly InjectedGroupComparer Instance = new();

        public bool Equals((string DeclaringFile, string Package) x, (string DeclaringFile, string Package) y)
        {
            return string.Equals(x.DeclaringFile, y.DeclaringFile, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Package, y.Package, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string DeclaringFile, string Package) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DeclaringFile),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Package)
            );
        }
    }

    /// <summary>
    /// Whether the package is development-only as a particular project resolves it: the convention
    /// answers by id, and the nuspec scan — when a scan was attached — answers by the version that
    /// project actually gets, so a project pinned to a non-dev version is not flagged on another
    /// project's nuspec.
    /// </summary>
    private static bool IsDevelopmentOnly(
        string packageName,
        string? projectPath,
        DevelopmentDependencyScanResult? scan
    )
    {
        if (IsDevelopmentOnlyByConvention(packageName))
        {
            return true;
        }

        return projectPath is not null
            && scan is not null
            && scan.Projects.Contains(new ProjectDevelopmentDependency(projectPath, packageName));
    }

    /// <summary>
    /// Whether a central pin's package is development-only: the convention answers by id, and the
    /// nuspec scan answers by the pin's own version — a version a range or unreadable spec cannot
    /// identify is left to the convention rather than guessed at.
    /// </summary>
    private static bool IsDevelopmentOnlyPin(
        string packageName,
        string? pinVersion,
        DevelopmentDependencyScanResult? scan
    )
    {
        if (IsDevelopmentOnlyByConvention(packageName))
        {
            return true;
        }

        return scan is not null
            && pinVersion is not null
            && NuGetVersion.TryParse(pinVersion, out var parsed)
            && scan.Versions.Contains(
                new PackageVersionKey(packageName, parsed.ToNormalizedString().ToLowerInvariant())
            );
    }

    /// <summary>
    /// Whether the package id names something that only contributes at build or test time.
    /// </summary>
    private static bool IsDevelopmentOnlyByConvention(string packageName)
    {
        if (DevelopmentOnlyPackages.Contains(packageName))
        {
            return true;
        }

        // An id ending in .Analyzer or .Analyzers is a Roslyn analyzer package by convention — it
        // ships assemblies under analyzers/ consumed by the compiler, never by the runtime.
        if (
            packageName.EndsWith(".Analyzer", StringComparison.OrdinalIgnoreCase)
            || packageName.EndsWith(".Analyzers", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return DevelopmentOnlyPrefixes.Any(prefix =>
            packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// Whether a declaration scopes every asset private. NuGet's covering value is <c>all</c>;
    /// <c>*</c> is accepted as the same. Any other list scopes only the named asset kinds — the
    /// package still flows.
    /// </summary>
    private static bool HasCoveringPrivateAssets(PackageReference reference)
    {
        return reference.PrivateAssets is not null && CoversAll(reference.PrivateAssets);
    }

    private static bool CoversAll(string? privateAssets)
    {
        if (string.IsNullOrWhiteSpace(privateAssets))
        {
            return false;
        }

        return privateAssets
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token =>
                token.Equals("all", StringComparison.OrdinalIgnoreCase)
                || token.Equals("*", StringComparison.Ordinal)
            );
    }
}
