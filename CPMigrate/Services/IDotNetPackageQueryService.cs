using CPMigrate.Models;

namespace CPMigrate.Services;

public interface IDotNetPackageQueryService
{
    /// <param name="isolatedIntermediateDirectory">
    /// Where this invocation should put its MSBuild intermediate output, or null for the project's own
    /// <c>obj</c>. Supplied when several projects are queried at once, so that two sharing an assets file
    /// cannot overwrite each other's — see <see cref="DotNetPackageListOptions.IsolatedIntermediateDirectory"/>.
    /// </param>
    Task<(List<PackageReference> References, bool Success)> ScanResolvedPackagesAsync(
        string projectFilePath,
        bool includeTransitive = false,
        string? isolatedIntermediateDirectory = null);
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

