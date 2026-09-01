using CPMigrate.Models;
using CPMigrate.Services.Update;
using NuGet.Versioning;

namespace CPMigrate.Tests.TestDoubles;

/// <summary>
/// In-memory stand-in for <see cref="IUpdateCandidateSource"/>. Tests that used to stub
/// <c>GetLatestVersionAsync</c> call <see cref="SetLatest"/> instead; a package present in the
/// props file but missing from this map is treated as "not on any configured feed", which is
/// how a private-feed pin used to look "up to date" against nuget.org.
/// </summary>
internal sealed class FakeUpdateCandidateSource : IUpdateCandidateSource
{
    private readonly Dictionary<string, string> _latest = new(StringComparer.OrdinalIgnoreCase);

    public List<string> UnscannedProjects { get; } = [];
    public List<PackageUpdateEntry> ExtraUpdates { get; } = [];
    public int TransitivePackagesFound { get; set; }
    public bool TransitiveScanFailed { get; set; }
    public bool? LastIncludeTransitive { get; private set; }
    public bool? LastIncludePrerelease { get; private set; }
    public int FindCalls { get; private set; }

    public void SetLatest(string package, string version) => _latest[package] = version;

    public Task<UpdateCandidateScan> FindAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyDictionary<string, HashSet<string>> currentVersions,
        bool includeTransitive,
        bool includePrerelease,
        int maxParallelism)
    {
        FindCalls++;
        LastIncludeTransitive = includeTransitive;
        LastIncludePrerelease = includePrerelease;

        var updates = new List<PackageUpdateEntry>();
        foreach (var (name, versions) in currentVersions)
        {
            if (!_latest.TryGetValue(name, out var latestText))
            {
                continue;
            }

            var current = RestoreBackedUpdateCandidateSource.ResolveCurrentVersion(versions);
            if (current is null
                || !NuGetVersion.TryParse(current, out var currentNuGet)
                || !NuGetVersion.TryParse(latestText, out var latestNuGet))
            {
                continue;
            }

            var isMajor = latestNuGet.Major != currentNuGet.Major;
            updates.Add(
                new PackageUpdateEntry(
                    name,
                    current,
                    latestNuGet.ToNormalizedString(),
                    isMajor,
                    !isMajor
                )
            );
        }

        if (includeTransitive)
        {
            updates.AddRange(ExtraUpdates);
        }

        return Task.FromResult(
            new UpdateCandidateScan(
                updates,
                UnscannedProjects.ToList(),
                TransitivePackagesFound,
                TransitiveScanFailed
            )
        );
    }
}
