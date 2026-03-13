using CPMigrate.Models;

namespace CPMigrate.Services;

public interface IDotNetPackageQueryService
{
    Task<(List<PackageReference> References, bool Success)> ScanResolvedPackagesAsync(string projectFilePath, bool includeTransitive = false);
    Task<(List<PackageReference> References, bool Success)> ScanTransitivePackagesAsync(string projectFilePath);
    Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(string projectFilePath);
    Task<(List<OutdatedPackageInfo> Packages, bool Success)> ScanOutdatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false);
    Task<(List<DeprecatedPackageInfo> Packages, bool Success)> ScanDeprecatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false);
}

