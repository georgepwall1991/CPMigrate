using System.Collections.Concurrent;
using CPMigrate.Models;
using NuGet.Versioning;

namespace CPMigrate.Licensing;

/// <summary>
/// Finds which referenced packages declare <c>developmentDependency="true"</c> in their nuspec —
/// the package's own statement that it only contributes at build time, read from the global
/// packages folder the way <see cref="LicenseScanService"/> reads licenses.
///
/// The signal complements the convention set <c>DevelopmentDependencyLeakAnalyzer</c> matches on:
/// a dev-only tool under an ordinary name — <c>Nerdbank.GitVersioning</c>,
/// <c>Microsoft.NETFramework.ReferenceAssemblies</c> — escapes every suffix and prefix the rule
/// can reasonably guess, but its nuspec says what it is. Packages that are not restored have no
/// nuspec to read and are simply absent from the result: absence of evidence, not a failure —
/// unrestored projects are a normal state, and the convention set still applies.
/// </summary>
public sealed class DevelopmentDependencyScanService
{
    private readonly Func<string> _packagesFolder;
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DevelopmentDependencyScanService(Func<string>? packagesFolder = null)
    {
        _packagesFolder = packagesFolder ?? (() => GlobalPackagesFolder.Resolve());
    }

    /// <summary>
    /// Scans direct references for self-declared development dependencies. Two precisions come back:
    /// the (project, package) pairs whose resolved version declared it — the answer for project
    /// references — and the (package, version) pairs that declared it — the answer for central
    /// pins, which have a version but no project. Transitives are skipped: a package nobody
    /// declared cannot be leaking through a declaration.
    /// </summary>
    public DevelopmentDependencyScanResult Scan(IReadOnlyList<PackageReference> references)
    {
        var projects = new HashSet<ProjectDevelopmentDependency>();
        var versions = new HashSet<PackageVersionKey>();

        foreach (var reference in references)
        {
            if (reference.IsTransitive)
            {
                continue;
            }

            var version = NormalizeExactVersion(EffectiveVersion(reference));
            if (version is null || !IsDevelopmentDependency(reference.PackageName, version))
            {
                continue;
            }

            versions.Add(new PackageVersionKey(reference.PackageName, version));
            if (!string.IsNullOrEmpty(reference.ProjectPath))
            {
                projects.Add(new ProjectDevelopmentDependency(reference.ProjectPath, reference.PackageName));
            }
        }

        return new DevelopmentDependencyScanResult(projects, versions);
    }

    /// <summary>
    /// Whether one id/version's nuspec self-declares <c>developmentDependency="true"</c>. A nuspec
    /// that cannot be read — package not restored, cache pruned — reports false rather than
    /// guessing, the direction that never invents a finding.
    /// </summary>
    public bool IsDevelopmentDependency(string packageId, string version)
    {
        var normalized = NormalizeExactVersion(version);
        if (normalized is null)
        {
            return false;
        }

        return _cache.GetOrAdd(
            CacheKey(packageId, normalized),
            _ => ReadsAsDevelopmentDependency(packageId, normalized)
        );
    }

    private bool ReadsAsDevelopmentDependency(string packageId, string version)
    {
        var path = GlobalPackagesFolder.NuspecPath(_packagesFolder(), packageId, version);
        return NuspecLicenseReader.TryReadFile(path, out var nuspec)
            && nuspec!.DevelopmentDependency;
    }

    private static string EffectiveVersion(PackageReference reference)
    {
        return string.IsNullOrWhiteSpace(reference.VersionOverride)
            ? reference.Version
            : reference.VersionOverride;
    }

    private static string? NormalizeExactVersion(string version)
    {
        return NuGetVersion.TryParse(version, out var parsed)
            ? parsed.ToNormalizedString().ToLowerInvariant()
            : null;
    }

    private static string CacheKey(string packageId, string version)
    {
        return packageId + "/" + version;
    }
}
