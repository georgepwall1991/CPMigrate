using System.Collections.Concurrent;
using CPMigrate.Models;
using NuGet.Versioning;

namespace CPMigrate.Licensing;

/// <summary>
/// One run of <c>--licenses</c>: unique package versions are read once, every reference is
/// classified, and anything that could not be read is a failure rather than a clean miss.
/// </summary>
public sealed class LicenseScanService
{
    private readonly Func<string> _packagesFolder;
    private readonly ConcurrentDictionary<string, CachedNuspec> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LicenseScanService(Func<string>? packagesFolder = null)
    {
        _packagesFolder = packagesFolder ?? (() => GlobalPackagesFolder.Resolve());
    }

    public LicenseScanResult Scan(IReadOnlyList<PackageReference> references, bool includeTransitive)
    {
        var licenses = new List<LicenseInfo>();
        var failures = 0;

        foreach (var reference in references)
        {
            if (reference.IsTransitive && !includeTransitive)
            {
                continue;
            }

            var version = EffectiveVersion(reference);
            if (!IsExactVersion(version))
            {
                failures++;
                continue;
            }

            var cached = _cache.GetOrAdd(CacheKey(reference.PackageName, version), _ => Read(reference.PackageName, version));
            if (!cached.Success)
            {
                failures++;
                continue;
            }

            licenses.Add(
                new LicenseInfo(
                    reference.PackageName,
                    version,
                    reference.ProjectPath,
                    reference.ProjectName,
                    cached.License,
                    cached.Classification,
                    cached.LicenseType,
                    reference.IsTransitive
                )
            );
        }

        return new LicenseScanResult(licenses, failures);
    }

    private CachedNuspec Read(string packageId, string version)
    {
        var path = GlobalPackagesFolder.NuspecPath(_packagesFolder(), packageId, version);
        if (!NuspecLicenseReader.TryReadFile(path, out var nuspec))
        {
            return CachedNuspec.Failed;
        }

        var (license, classification) = Classify(nuspec!);
        return new CachedNuspec(true, license, classification, nuspec!.LicenseType);
    }

    private static (string License, LicenseClassification Classification) Classify(NuspecLicense nuspec)
    {
        if (nuspec.LicenseType == "expression" && !string.IsNullOrWhiteSpace(nuspec.Expression))
        {
            return (nuspec.Expression, LicenseRiskClassifier.ClassifyExpression(nuspec.Expression));
        }

        var display = nuspec.LicenseType switch
        {
            // Stryker disable once string : reader never yields a null file payload
            "file" => nuspec.Expression ?? "file",
            // Stryker disable once string : reader never yields a null url payload
            "url" => nuspec.LicenseUrl ?? "url",
            _ => "unknown",
        };

        return (display, LicenseClassification.Unknown);
    }

    private static string EffectiveVersion(PackageReference reference)
    {
        return string.IsNullOrWhiteSpace(reference.VersionOverride)
            ? reference.Version
            : reference.VersionOverride;
    }

    private static bool IsExactVersion(string version)
    {
        return NuGetVersion.TryParse(version, out _);
    }

    private static string CacheKey(string packageId, string version)
    {
        return packageId + "/" + version;
    }

    private sealed record CachedNuspec(
        bool Success,
        string License,
        LicenseClassification Classification,
        string LicenseType
    )
    {
        // Stryker disable once all : dummy fields are never read on the failure path
        public static CachedNuspec Failed { get; } = new(false, "", LicenseClassification.Unknown, "missing");
    }
}

/// <summary>Successful classifications plus the number of references that could not be read.</summary>
public sealed record LicenseScanResult(IReadOnlyList<LicenseInfo> Licenses, int Failures);
