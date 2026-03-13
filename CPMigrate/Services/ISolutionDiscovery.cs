namespace CPMigrate.Services;

public interface ISolutionDiscovery
{
    Task<(string BasePath, List<string> ProjectPaths)> DiscoverProjectsFromSolutionAsync(string solutionPath);
    (string BasePath, List<string> ProjectPaths) DiscoverProjectsFromSolution(string solutionPath);
    (string BasePath, List<string> ProjectPaths) DiscoverProjectFromPath(string projectPath);
    string[] GetSolutionFiles(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly);
}

