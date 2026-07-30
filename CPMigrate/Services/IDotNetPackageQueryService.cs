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
    /// <param name="isolatedIntermediateDirectory">
    /// Where this invocation should write its MSBuild intermediate output, or null for the project's own
    /// <c>obj</c>. These queries restore too, so two projects sharing an assets file corrupt each other's
    /// results — a project can be reported with another project's package versions, and therefore with
    /// another project's vulnerabilities.
    /// </param>
    Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(
        string projectFilePath,
        string? isolatedIntermediateDirectory = null);
    Task<(List<OutdatedPackageInfo> Packages, bool Success)> ScanOutdatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false,
        string? isolatedIntermediateDirectory = null);
    Task<(List<DeprecatedPackageInfo> Packages, bool Success)> ScanDeprecatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false,
        string? isolatedIntermediateDirectory = null);
}

