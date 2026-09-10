using CPMigrate.Models;

namespace CPMigrate.Services;

public interface ISolutionDiscovery
{
    Task<(string BasePath, List<string> ProjectPaths)> DiscoverProjectsFromSolutionAsync(string solutionPath);
    (string BasePath, List<string> ProjectPaths) DiscoverProjectsFromSolution(string solutionPath);
    (string BasePath, List<string> ProjectPaths) DiscoverProjectFromPath(string projectPath);
    string[] GetSolutionFiles(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>
    /// The same discovery <see cref="DiscoverProjectsFromSolutionAsync"/> performs, with the
    /// projects the solution names but the filesystem does not have carried as data instead of
    /// only as a console warning — a warning a machine-readable run cannot afford to drop.
    /// </summary>
    Task<DiscoveryResult> DiscoverProjectsDetailedAsync(string solutionPath);
}

