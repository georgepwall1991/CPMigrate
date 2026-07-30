using CPMigrate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CPMigrate.Services;

/// <summary>
/// Analyzes .NET projects and solutions to extract package references.
/// </summary>
public partial class ProjectAnalyzer : IProjectAnalyzer
{
    private readonly IProjectFileScanner _projectFileScanner;
    private readonly IDotNetPackageQueryService _packageQueryService;
    private readonly ISolutionDiscovery _solutionDiscovery;

    public ProjectAnalyzer(
        IConsoleService consoleService,
        IDotNetCliService? dotNetCliService = null,
        ILogger<ProjectAnalyzer>? logger = null)
        : this(
            consoleService,
            new SolutionDiscovery(consoleService),
            new ProjectFileScanner(consoleService),
            new DotNetPackageQueryService(consoleService, dotNetCliService),
            logger)
    {
    }

    public ProjectAnalyzer(
        IConsoleService consoleService,
        ISolutionDiscovery solutionDiscovery,
        IProjectFileScanner projectFileScanner,
        IDotNetPackageQueryService packageQueryService,
        ILogger<ProjectAnalyzer>? logger = null)
    {
        _solutionDiscovery = solutionDiscovery;
        _projectFileScanner = projectFileScanner;
        _packageQueryService = packageQueryService;
        _ = logger ?? NullLogger<ProjectAnalyzer>.Instance;
    }

    public Task<(string BasePath, List<string> ProjectPaths)> DiscoverProjectsFromSolutionAsync(string solutionPath)
    {
        return _solutionDiscovery.DiscoverProjectsFromSolutionAsync(solutionPath);
    }

    public (string BasePath, List<string> ProjectPaths) DiscoverProjectsFromSolution(string solutionPath)
    {
        return _solutionDiscovery.DiscoverProjectsFromSolution(solutionPath);
    }

    public (string BasePath, List<string> ProjectPaths) DiscoverProjectFromPath(string projectPath)
    {
        return _solutionDiscovery.DiscoverProjectFromPath(projectPath);
    }

    public static string GetTargetFramework(string projectFilePath)
    {
        return new ProjectFileScanner(SilentConsoleService.Instance).GetTargetFramework(projectFilePath);
    }

    public static string ProcessProject(
        string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false)
    {
        return new ProjectFileScanner(SilentConsoleService.Instance)
            .ProcessProject(projectFilePath, packageVersions, keepVersionAttributes);
    }

    public bool ScanProjectPackages(string projectFilePath, Dictionary<string, HashSet<string>> packageVersions)
    {
        var (refs, success) = ScanProjectPackages(projectFilePath);
        if (!success)
        {
            return false;
        }

        foreach (var reference in refs)
        {
            if (packageVersions.TryGetValue(reference.PackageName, out var versions))
            {
                versions.Add(reference.Version);
            }
            else
            {
                packageVersions.Add(reference.PackageName, [reference.Version]);
            }
        }

        return true;
    }

    public (List<PackageReference> References, bool Success) ScanProjectPackages(string projectFilePath)
    {
        return _projectFileScanner.ScanProjectPackages(projectFilePath);
    }

    /// <inheritdoc />
    public (List<PackageReference> References, bool Success) ScanDeclaredPackages(
        string projectFilePath
    ) => _projectFileScanner.ScanDeclaredPackages(projectFilePath);

    public static string[] GetSolutionFiles(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return new SolutionDiscovery(SilentConsoleService.Instance).GetSolutionFiles(directory, searchOption);
    }
}
