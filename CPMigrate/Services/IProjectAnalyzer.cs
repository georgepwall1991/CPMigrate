using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Interface for analyzing .NET projects and solutions.
/// </summary>
public interface IProjectAnalyzer
{
    /// <summary>
    /// Discovers project paths from a solution file or directory.
    /// </summary>
    (string BasePath, List<string> ProjectPaths) DiscoverProjectsFromSolution(string solutionPath);

    /// <summary>
    /// Discovers project paths from a solution file or directory (async version).
    /// </summary>
    Task<(string BasePath, List<string> ProjectPaths)> DiscoverProjectsFromSolutionAsync(string solutionPath);

    /// <summary>
    /// Discovers project path from a directory or direct file path.
    /// </summary>
    (string BasePath, List<string> ProjectPaths) DiscoverProjectFromPath(string projectPath);

    /// <summary>
    /// Scans a project for direct package references.
    /// </summary>
    (List<PackageReference> References, bool Success) ScanProjectPackages(string projectFilePath);

    /// <summary>
    /// Scans a project file and populates a package versions dictionary.
    /// </summary>
    bool ScanProjectPackages(string projectFilePath, Dictionary<string, HashSet<string>> packageVersions);

    /// <summary>
    /// Scans a project for resolved package references via dotnet package list JSON output.
    /// </summary>
    Task<(List<PackageReference> References, bool Success)> ScanResolvedPackagesAsync(string projectFilePath, bool includeTransitive = false);

    /// <summary>
    /// Scans a project for transitive package dependencies.
    /// </summary>
    Task<(List<PackageReference> References, bool Success)> ScanTransitivePackagesAsync(string projectFilePath);

    /// <summary>
    /// Scans a project for package vulnerabilities.
    /// </summary>
    Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(string projectFilePath);

    /// <summary>
    /// Scans a project for outdated packages.
    /// </summary>
    Task<(List<OutdatedPackageInfo> Packages, bool Success)> ScanOutdatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false);

    /// <summary>
    /// Scans a project for deprecated packages.
    /// </summary>
    Task<(List<DeprecatedPackageInfo> Packages, bool Success)> ScanDeprecatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false);
}
