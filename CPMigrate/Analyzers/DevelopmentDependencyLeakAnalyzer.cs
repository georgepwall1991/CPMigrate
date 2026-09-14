using CPMigrate.Models;

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
/// The dev-only set is deliberately convention-based rather than a feed lookup: a package id ending
/// in <c>.Analyzer</c>/<c>.Analyzers</c> ships analyzer assets by definition, and the test and
/// coverage packages named here never contribute runtime surface. A package that escapes the
/// convention — a dev-only tool under an ordinary name — is out of scope for this rule rather than
/// guessed at.
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

        // A GlobalPackageReference is itself a declaration: it injects the package into every
        // governed project, so when it is dev-only and unscoped the leak lives in the props file.
        foreach (var pin in pins)
        {
            if (
                pin.IsGlobal
                && IsDevelopmentOnly(pin.Package)
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
            if (!IsDevelopmentOnly(packageGroup.Key) || centralCoverage.Contains(packageGroup.Key))
            {
                continue;
            }

            // Per project: the group must contain a real declaration (a metadata-only Update alone
            // does not create the reference) and no member may scope every asset private.
            var uncovered = packageGroup
                .GroupBy(
                    reference => packageInfo.ProjectId(reference.ProjectPath),
                    StringComparer.Ordinal
                )
                .Where(projectGroup => projectGroup.Any(reference => !reference.IsMetadataOnlyUpdate))
                .Where(projectGroup => !projectGroup.Any(HasCoveringPrivateAssets))
                .Select(projectGroup => projectGroup.Key)
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
    /// Whether the package id names something that only contributes at build or test time.
    /// </summary>
    private static bool IsDevelopmentOnly(string packageName)
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
