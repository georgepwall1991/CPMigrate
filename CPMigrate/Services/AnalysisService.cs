using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates package analysis by running all registered analyzers.
/// </summary>
public class AnalysisService : IAnalysisService
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers;

    private static readonly IComparer<PackageReference> ReferenceOrder =
        Comparer<PackageReference>.Create((left, right) =>
        {
            var result = string.CompareOrdinal(left.PackageName, right.PackageName);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.Version, right.Version);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ProjectPath, right.ProjectPath);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ProjectName, right.ProjectName);
            if (result != 0)
            {
                return result;
            }

            result = left.IsTransitive.CompareTo(right.IsTransitive);
            if (result != 0)
            {
                return result;
            }

            result = left.IsConditional.CompareTo(right.IsConditional);
            return result != 0
                ? result
                : string.CompareOrdinal(left.VersionOverride, right.VersionOverride);
        });

    private static readonly IComparer<VulnerabilityInfo> VulnerabilityOrder =
        Comparer<VulnerabilityInfo>.Create((left, right) =>
        {
            var result = string.CompareOrdinal(left.PackageName, right.PackageName);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.Id, right.Id);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ResolvedVersion, right.ResolvedVersion);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.FixedVersion, right.FixedVersion);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.Severity, right.Severity);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ProjectPath, right.ProjectPath);
            return result != 0 ? result : string.CompareOrdinal(left.ProjectName, right.ProjectName);
        });

    private static readonly IComparer<OutdatedPackageInfo> OutdatedOrder =
        Comparer<OutdatedPackageInfo>.Create((left, right) =>
        {
            var result = string.CompareOrdinal(left.PackageName, right.PackageName);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ResolvedVersion, right.ResolvedVersion);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.LatestVersion, right.LatestVersion);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ProjectPath, right.ProjectPath);
            return result != 0 ? result : string.CompareOrdinal(left.ProjectName, right.ProjectName);
        });

    private static readonly IComparer<DeprecatedPackageInfo> DeprecatedOrder =
        Comparer<DeprecatedPackageInfo>.Create((left, right) =>
        {
            var result = string.CompareOrdinal(left.PackageName, right.PackageName);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ResolvedVersion, right.ResolvedVersion);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ProjectPath, right.ProjectPath);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(
                string.Join('\u0001', left.Reasons),
                string.Join('\u0001', right.Reasons)
            );
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.AlternativePackage, right.AlternativePackage);
            return result != 0
                ? result
                : string.CompareOrdinal(left.AlternativeVersionRange, right.AlternativeVersionRange);
        });

    private static readonly IComparer<LicenseInfo> LicenseOrder =
        Comparer<LicenseInfo>.Create((left, right) =>
        {
            var result = string.CompareOrdinal(left.PackageName, right.PackageName);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.Version, right.Version);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ProjectPath, right.ProjectPath);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.License, right.License);
            return result != 0 ? result : left.Classification.CompareTo(right.Classification);
        });

    private static readonly IComparer<AnalysisIssue> IssueOrder =
        Comparer<AnalysisIssue>.Create((left, right) =>
        {
            var result = left.IssueCode.CompareTo(right.IssueCode);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.PackageName, right.PackageName);
            return result != 0 ? result : string.CompareOrdinal(left.Description, right.Description);
        });

    private static readonly IComparer<string> StringOrder =
        Comparer<string>.Create(string.CompareOrdinal);

    public AnalysisService()
        : this(AnalyzerCatalog.CreateDefault(SilentConsoleService.Instance))
    {
    }

    public AnalysisService(IEnumerable<IAnalyzer> analyzers)
    {
        _analyzers = analyzers.ToList();
    }

    /// <summary>
    /// Runs all analyzers on the provided package information.
    /// </summary>
    /// <param name="packageInfo">Package references collected from projects.</param>
    /// <returns>Combined analysis report from all analyzers.</returns>
    /// <remarks>
    /// The report is byte-stable across runs on identical scan data, whatever order concurrent
    /// scan workers merged the per-project batches in: every input sequence is canonicalized
    /// before analysis (so grouping-derived content — descriptions, metadata samples, first-match
    /// picks — cannot drift either) and each result's findings are emitted in
    /// (issue code, package name) order. A CI job diffing two reports sees only real changes.
    /// </remarks>
    public AnalysisReport Analyze(ProjectPackageInfo packageInfo)
    {
        var canonical = Canonicalize(packageInfo);

        var results = _analyzers
            .Select(analyzer => analyzer.Analyze(canonical))
            .Select(CanonicalizeIssues)
            .ToList();

        return new AnalysisReport(
            canonical.ProjectCount,
            canonical.TotalReferences,
            results
        );
    }

    /// <summary>
    /// Puts every sequence an analyzer iterates into one total order derived only from the data,
    /// never from the arrival order of the batches that produced it. Sequences already in order
    /// are passed through untouched.
    /// </summary>
    private static ProjectPackageInfo Canonicalize(ProjectPackageInfo packageInfo)
    {
        var canonical = packageInfo;

        if (packageInfo.References is not null)
        {
            canonical = canonical with
            {
                // References is contractually non-null; the nullable return exists for the
                // genuinely-optional collections below.
                References = Ordered(packageInfo.References, ReferenceOrder)!,
            };
        }

        if (packageInfo.Vulnerabilities is not null)
        {
            canonical = canonical with { Vulnerabilities = Ordered(packageInfo.Vulnerabilities, VulnerabilityOrder) };
        }

        if (packageInfo.OutdatedPackages is not null)
        {
            canonical = canonical with { OutdatedPackages = Ordered(packageInfo.OutdatedPackages, OutdatedOrder) };
        }

        if (packageInfo.DeprecatedPackages is not null)
        {
            canonical = canonical with { DeprecatedPackages = Ordered(packageInfo.DeprecatedPackages, DeprecatedOrder) };
        }

        if (packageInfo.ScannedProjects is not null)
        {
            canonical = canonical with { ScannedProjects = Ordered(packageInfo.ScannedProjects, StringOrder) };
        }

        // DeclaredReferences: order the PROJECT BATCHES deterministically but never reorder
        // declarations inside a batch — conditional-metadata resolution (a non-empty Version
        // followed by a clearing Version="") depends on declaration sequence. A stable sort by
        // project path groups batches identically whatever order they arrived in, while leaving
        // every project's own sequence untouched.

        if (packageInfo.DeclaredReferences is not null)
        {
            canonical = canonical with
            {
                DeclaredReferences = packageInfo
                    .DeclaredReferences.OrderBy(reference => reference.ProjectPath, StringComparer.Ordinal)
                    .ToList(),
            };
        }

        if (packageInfo.Licenses is not null)
        {
            canonical = canonical with { Licenses = Ordered(packageInfo.Licenses, LicenseOrder) };
        }

        return canonical;
    }

    /// <summary>
    /// Re-emits one analyzer's findings in (issue code, package name) order, whatever sequence
    /// its grouping happened to visit them in. Findings tied on all three keys keep their
    /// relative order, which the canonicalized inputs have already made deterministic.
    /// </summary>
    private static AnalyzerResult CanonicalizeIssues(AnalyzerResult result)
    {
        var issues = result.Issues;
        for (var index = 1; index < issues.Count; index++)
        {
            if (IssueOrder.Compare(issues[index - 1], issues[index]) > 0)
            {
                return result with { Issues = issues.OrderBy(issue => issue, IssueOrder).ToList() };
            }
        }

        return result;
    }

    private static IReadOnlyList<T>? Ordered<T>(IReadOnlyList<T>? items, IComparer<T> comparer)
    {
        // Null is meaningful: GetProjectsScanned/GetDeclaredReferences fall back to References on
        // null, and erasing that state would fabricate findings for inputs that never declared them.
        if (items is null)
        {
            return null;
        }

        if (items.Count < 2)
        {
            return items;
        }

        for (var index = 1; index < items.Count; index++)
        {
            if (comparer.Compare(items[index - 1], items[index]) > 0)
            {
                return items.OrderBy(item => item, comparer).ToList();
            }
        }

        return items;
    }

}
