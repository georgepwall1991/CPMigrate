using CPMigrate.Models;
using NuGet.Versioning;

namespace CPMigrate.Services.Update;

/// <summary>
/// Asks each project what is outdated the same way restore does: <c>dotnet list package --outdated</c>
/// honours NuGet.Config sources, credential providers, and package source mapping. A hardcoded
/// nuget.org lookup cannot, and that is how a private-feed package used to look "up to date"
/// (or, worse, how a squatted nuget.org version used to look like the upgrade to take).
/// </summary>
public sealed class RestoreBackedUpdateCandidateSource : IUpdateCandidateSource
{
    private readonly IProjectAnalyzer _projectAnalyzer;

    public RestoreBackedUpdateCandidateSource(IProjectAnalyzer projectAnalyzer)
    {
        _projectAnalyzer = projectAnalyzer;
    }

    /// <inheritdoc />
    public async Task<UpdateCandidateScan> FindAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyDictionary<string, HashSet<string>> currentVersions,
        bool includeTransitive,
        bool includePrerelease,
        int maxParallelism)
    {
        if (projectPaths.Count == 0)
        {
            return new UpdateCandidateScan([], [], TransitivePackagesFound: 0);
        }

        var concurrency = Math.Max(1, maxParallelism);
        var results = await GroupedScanScheduler.RunAsync(
            projectPaths,
            concurrency,
            (index, projectPath, _) => ScanProjectAsync(
                projectPath,
                includeTransitive,
                includePrerelease
            )
        );

        var unscanned = new List<string>();
        var outdated = new List<OutdatedPackageInfo>();
        var transitiveRefs = new List<PackageReference>();
        var anyTransitiveSuccess = false;
        var anyTransitiveAttempted = false;

        for (var i = 0; i < results.Length; i++)
        {
            var result = results[i];
            if (!result.OutdatedSuccess)
            {
                unscanned.Add(Path.GetFileName(projectPaths[i]));
            }
            else
            {
                outdated.AddRange(result.Outdated);
            }

            if (includeTransitive)
            {
                anyTransitiveAttempted = true;
                if (result.TransitiveSuccess)
                {
                    anyTransitiveSuccess = true;
                    transitiveRefs.AddRange(result.Transitive);
                }
            }
        }

        var updates = MergeCandidates(outdated, currentVersions, includeTransitive);
        var transitiveFound = CountUniqueTransitive(transitiveRefs);
        var transitiveFailed = anyTransitiveAttempted && !anyTransitiveSuccess;

        return new UpdateCandidateScan(updates, unscanned, transitiveFound, transitiveFailed);
    }

    private async Task<ProjectScan> ScanProjectAsync(
        string projectPath,
        bool includeTransitive,
        bool includePrerelease)
    {
        var (outdated, outdatedSuccess) = await _projectAnalyzer.ScanOutdatedPackagesAsync(
            projectPath,
            includeTransitive,
            includePrerelease
        );

        if (!includeTransitive)
        {
            return new ProjectScan(outdated, outdatedSuccess, [], TransitiveSuccess: false);
        }

        try
        {
            var (transitive, transitiveSuccess) = await _projectAnalyzer.ScanTransitivePackagesAsync(
                projectPath
            );
            return new ProjectScan(outdated, outdatedSuccess, transitive, transitiveSuccess);
        }
        catch (Exception)
        {
            // A thrown transitive inventory must not take down candidate discovery: the outdated
            // scan is the one that decides whether the run may write, and the caller already has
            // a path for "transitive count unavailable, continue with directs".
            return new ProjectScan(outdated, outdatedSuccess, [], TransitiveSuccess: false);
        }
    }

    /// <summary>
    /// Collapses per-project, per-TFM outdated rows into one entry per package id. Highest latest
    /// version wins so a multi-targeted solution does not propose the lower of two available
    /// upgrades; the props pin is the current version for anything already central, so we never
    /// "upgrade" to a resolved version that restore already selected below the pin.
    /// </summary>
    internal static IReadOnlyList<PackageUpdateEntry> MergeCandidates(
        IReadOnlyList<OutdatedPackageInfo> outdated,
        IReadOnlyDictionary<string, HashSet<string>> currentVersions,
        bool includeTransitive)
    {
        var accumulators = new Dictionary<string, CandidateAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in outdated)
        {
            var isDirect = currentVersions.ContainsKey(row.PackageName);
            if (!isDirect && !includeTransitive)
            {
                continue;
            }

            if (!accumulators.TryGetValue(row.PackageName, out var acc))
            {
                acc = new CandidateAccumulator(row.PackageName, isDirect);
                accumulators[row.PackageName] = acc;
            }

            acc.Consider(row);
        }

        var updates = new List<PackageUpdateEntry>(accumulators.Count);
        foreach (var acc in accumulators.Values.OrderBy(a => a.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            var latest = acc.Latest;
            if (latest is null)
            {
                continue;
            }

            var current = acc.IsDirect
                ? ResolveCurrentVersion(currentVersions[acc.PackageName])
                : acc.HighestResolved?.ToNormalizedString();

            if (current is null)
            {
                continue;
            }

            var currentNuGet = NuGetVersion.TryParse(current, out var parsed) ? parsed : null;
            if (currentNuGet is null)
            {
                continue;
            }

            var isMajor = latest.Major != currentNuGet.Major;
            updates.Add(
                new PackageUpdateEntry(
                    acc.PackageName,
                    current,
                    latest.ToNormalizedString(),
                    isMajor,
                    !isMajor,
                    IsTransitive: !acc.IsDirect
                )
            );
        }

        return updates;
    }

    internal static string? ResolveCurrentVersion(IReadOnlyCollection<string> versions)
    {
        if (versions.Count == 1)
        {
            return versions.First();
        }

        return versions
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v != null)
            .OrderByDescending(v => v)
            .FirstOrDefault()
            ?.ToNormalizedString();
    }

    private static int CountUniqueTransitive(List<PackageReference> references)
    {
        return references
            .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
                g.Select(r => NuGetVersion.TryParse(r.Version, out var parsed) ? parsed : null)
                    .Any(v => v != null)
            )
            .Count(hasParsed => hasParsed);
    }

    private sealed class CandidateAccumulator(string packageName, bool isDirect)
    {
        public string PackageName { get; } = packageName;
        public bool IsDirect { get; } = isDirect;
        public NuGetVersion? Latest { get; private set; }
        public NuGetVersion? HighestResolved { get; private set; }

        public void Consider(OutdatedPackageInfo row)
        {
            if (NuGetVersion.TryParse(row.LatestVersion, out var latest)
                && (Latest is null || latest > Latest))
            {
                Latest = latest;
            }

            if (NuGetVersion.TryParse(row.ResolvedVersion, out var resolved)
                && (HighestResolved is null || resolved > HighestResolved))
            {
                HighestResolved = resolved;
            }
        }
    }

    private readonly record struct ProjectScan(
        List<OutdatedPackageInfo> Outdated,
        bool OutdatedSuccess,
        List<PackageReference> Transitive,
        bool TransitiveSuccess);
}
