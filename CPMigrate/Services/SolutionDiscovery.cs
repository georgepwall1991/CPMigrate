using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace CPMigrate.Services;

public sealed class SolutionDiscovery : ISolutionDiscovery
{
    private readonly IConsoleService _consoleService;

    public SolutionDiscovery(IConsoleService consoleService)
    {
        _consoleService = consoleService;
    }

    public async Task<(string BasePath, List<string> ProjectPaths)> DiscoverProjectsFromSolutionAsync(string solutionPath)
    {
        var projectPaths = new List<string>();
        var fullPath = ResolveSolutionFilePath(solutionPath);

        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            var message = fullPath == null
                ? "No solution file found in the specified directory."
                : "Solution file not found.";
            _consoleService.Info(message);
            return (string.Empty, projectPaths);
        }

        var basePath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(basePath))
        {
            _consoleService.Error("Invalid solution path: cannot determine directory.");
            return (string.Empty, projectPaths);
        }

        try
        {
            if (!await DiscoverProjectsInSolutionAsync(fullPath, basePath, projectPaths))
            {
                return (string.Empty, projectPaths);
            }
        }
        catch (Exception ex)
        {
#pragma warning disable S2139
            _consoleService.Error($"Failed to parse solution file: {ex.Message}");
            throw;
#pragma warning restore S2139
        }

        return (basePath, projectPaths);
    }

    public (string BasePath, List<string> ProjectPaths) DiscoverProjectsFromSolution(string solutionPath)
    {
        return DiscoverProjectsFromSolutionAsync(solutionPath).GetAwaiter().GetResult();
    }

    public (string BasePath, List<string> ProjectPaths) DiscoverProjectFromPath(string projectPath)
    {
        var projectPaths = new List<string>();
        var fullPath = Path.GetFullPath(projectPath);

        if (Directory.Exists(fullPath))
        {
            var projFile = Directory.EnumerateFiles(fullPath, "*.*proj")
                .FirstOrDefault(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".csproj" or ".fsproj" or ".vbproj";
                });

            if (projFile == null)
            {
                _consoleService.Info("No project file found in the specified directory.");
                return (string.Empty, projectPaths);
            }

            fullPath = projFile;
        }

        if (!File.Exists(fullPath))
        {
            _consoleService.Info("Project file not found.");
            return (string.Empty, projectPaths);
        }

        projectPaths.Add(fullPath);
        var basePath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(basePath))
        {
            _consoleService.Error("Invalid project path: cannot determine directory.");
            return (string.Empty, []);
        }

        return (basePath, projectPaths);
    }

    public string[] GetSolutionFiles(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.GetFiles(directory, "*.sln", searchOption)
            .Concat(Directory.GetFiles(directory, "*.slnx", searchOption))
            .ToArray();
    }

    private string? ResolveSolutionFilePath(string solutionPath)
    {
        var fullPath = Path.GetFullPath(solutionPath);

        if (!Directory.Exists(fullPath))
        {
            return fullPath;
        }

        var slnFiles = GetSolutionFiles(fullPath);
        if (slnFiles.Length == 0)
        {
            return null;
        }

        if (slnFiles.Length == 1)
        {
            return slnFiles[0];
        }

        var selected = PromptForSolutionSelection(slnFiles);
        return File.Exists(selected) ? selected : null;
    }

    private async Task<bool> DiscoverProjectsInSolutionAsync(string solutionFullPath, string basePath, List<string> projectPaths)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFullPath);
        if (serializer == null)
        {
            _consoleService.Error($"Unsupported solution file format: {Path.GetExtension(solutionFullPath)}");
            return false;
        }

        var solution = await serializer.OpenAsync(solutionFullPath, CancellationToken.None);

        var validProjects = solution.SolutionProjects
            .Where(p => !string.IsNullOrEmpty(p.FilePath))
            .Select(p => (Project: p, Extension: GetSafeExtension(p.FilePath)))
            .Where(t => t.Extension is ".csproj" or ".fsproj" or ".vbproj");

        foreach (var (project, _) in validProjects)
        {
            var absolutePath = Path.GetFullPath(Path.Combine(basePath, project.FilePath));
            if (File.Exists(absolutePath))
            {
                projectPaths.Add(absolutePath);
                _consoleService.Info($"Found project: {Path.GetFileNameWithoutExtension(project.FilePath)}");
            }
            else
            {
                _consoleService.Warning($"Project found in solution but file missing: {absolutePath}");
            }
        }

        return true;
    }

    private string PromptForSolutionSelection(string[] slnFiles)
    {
        var choices = slnFiles.Select(f => Path.GetFileName(f) ?? f).ToList();
        var selection = _consoleService.AskSelection(
            "Multiple solution files found. Which one would you like to use?",
            choices);

        return slnFiles.First(f => Path.GetFileName(f) == selection);
    }

    private static string? GetSafeExtension(string filePath)
    {
        try
        {
            return Path.GetExtension(filePath)?.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

