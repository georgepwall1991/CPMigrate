using CPMigrate.Models;

namespace CPMigrate.Services;

public partial class ProjectAnalyzer
{
    public Task<(List<PackageReference> References, bool Success)> ScanResolvedPackagesAsync(
        string projectFilePath,
        bool includeTransitive = false,
        string? isolatedIntermediateDirectory = null
    )
    {
        return _packageQueryService.ScanResolvedPackagesAsync(
            projectFilePath,
            includeTransitive,
            isolatedIntermediateDirectory
        );
    }

    public Task<(List<PackageReference> References, bool Success)> ScanTransitivePackagesAsync(string projectFilePath)
    {
        return _packageQueryService.ScanTransitivePackagesAsync(projectFilePath);
    }

    public Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(string projectFilePath)
    {
        return _packageQueryService.ScanVulnerabilitiesAsync(projectFilePath);
    }

    public Task<(List<OutdatedPackageInfo> Packages, bool Success)> ScanOutdatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false)
    {
        return _packageQueryService.ScanOutdatedPackagesAsync(projectFilePath, includeTransitive, includePrerelease);
    }

    public Task<(List<DeprecatedPackageInfo> Packages, bool Success)> ScanDeprecatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false)
    {
        return _packageQueryService.ScanDeprecatedPackagesAsync(projectFilePath, includeTransitive, includePrerelease);
    }

    internal static List<PackageReference> ParsePackageReferencesFromJson(
        string output,
        string projectFilePath,
        string projectName,
        bool includeTransitive)
    {
        return DotNetPackageQueryService.ParsePackageReferencesFromJson(output, projectFilePath, projectName, includeTransitive);
    }

    internal static List<VulnerabilityInfo> ParseVulnerabilitiesFromJson(string output, string projectName)
    {
        return DotNetPackageQueryService.ParseVulnerabilitiesFromJson(output, projectName);
    }

    internal static List<OutdatedPackageInfo> ParseOutdatedPackagesFromJson(
        string output,
        string projectFilePath,
        string projectName,
        bool includeTransitive)
    {
        return DotNetPackageQueryService.ParseOutdatedPackagesFromJson(output, projectFilePath, projectName, includeTransitive);
    }

    internal static List<DeprecatedPackageInfo> ParseDeprecatedPackagesFromJson(
        string output,
        string projectFilePath,
        string projectName,
        bool includeTransitive)
    {
        return DotNetPackageQueryService.ParseDeprecatedPackagesFromJson(output, projectFilePath, projectName, includeTransitive);
    }
}
