using CPMigrate.Models;
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
        var result = await DiscoverProjectsDetailedAsync(solutionPath);
        return (result.BasePath, result.ProjectPaths);
    }

    public async Task<DiscoveryResult> DiscoverProjectsDetailedAsync(string solutionPath)
    {
        var projectPaths = new List<string>();
        var missingProjects = new List<string>();
        var fullPath = ResolveSolutionFilePath(solutionPath, out var ambiguous);

        if (ambiguous)
        {
            // Several solutions and no way to choose — the refusal was already reported; scanning
            // the whole directory instead would sweep up projects no solution names.
            return new DiscoveryResult(string.Empty, projectPaths, missingProjects);
        }

        if (fullPath == null)
        {
            // The target is a directory holding no solution file — but it may still hold
            // projects. Scanning it is what pointing a tool at a folder means; anchoring the
            // result at that directory also keeps props-file lookups honest instead of falling
            // back to the process working directory.
            var discovered = DiscoverProjectsInDirectory(Path.GetFullPath(solutionPath));
            if (discovered.Count == 0)
            {
                _consoleService.Info("No solution file found in the specified directory.");
                return new DiscoveryResult(string.Empty, projectPaths, missingProjects);
            }

            _consoleService.Info(
                $"No solution file found; discovered {discovered.Count} project(s) in the directory tree.");
            projectPaths.AddRange(discovered);
            return new DiscoveryResult(Path.GetFullPath(solutionPath), projectPaths, missingProjects);
        }

        if (!File.Exists(fullPath))
        {
            _consoleService.Info("Solution file not found.");
            return new DiscoveryResult(string.Empty, projectPaths, missingProjects);
        }

        // A project file passed as the target is a single-project discovery — only the
        // serializer check below would otherwise reject it as an unsupported format.
        if (IsProjectFile(fullPath))
        {
            _consoleService.Info($"Found project: {Path.GetFileNameWithoutExtension(fullPath)}");
            projectPaths.Add(fullPath);
            var projectDir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(projectDir))
            {
                _consoleService.Error("Invalid project path: cannot determine directory.");
                return new DiscoveryResult(string.Empty, projectPaths, missingProjects);
            }

            return new DiscoveryResult(projectDir, projectPaths, missingProjects);
        }

        var basePath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(basePath))
        {
            _consoleService.Error("Invalid solution path: cannot determine directory.");
            return new DiscoveryResult(string.Empty, projectPaths, missingProjects);
        }

        try
        {
            if (!await DiscoverProjectsInSolutionAsync(fullPath, basePath, projectPaths, missingProjects))
            {
                return new DiscoveryResult(string.Empty, projectPaths, missingProjects);
            }
        }
        catch (Exception ex)
        {
#pragma warning disable S2139
            _consoleService.Error($"Failed to parse solution file: {ex.Message}");
            throw;
#pragma warning restore S2139
        }

        return new DiscoveryResult(basePath, projectPaths, missingProjects);
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

    /// <summary>
    /// Resolves the target to one solution file. <paramref name="ambiguous"/> distinguishes
    /// "directory with no solution" (null — the directory scan is a fair fallback) from
    /// "several solutions and no answer" (null — falling back would ignore the refusal).
    /// </summary>
    private string? ResolveSolutionFilePath(string solutionPath, out bool ambiguous)
    {
        ambiguous = false;
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
        if (selected != null && File.Exists(selected))
        {
            return selected;
        }

        ambiguous = true;
        return null;
    }

    private async Task<bool> DiscoverProjectsInSolutionAsync(string solutionFullPath, string basePath, List<string> projectPaths, List<string> missingProjects)
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
                // Carried as data as well as warned: a machine-readable run silences this console,
                // and a missing project that vanishes entirely reads as "scanned, absent" about a
                // project nobody opened.
                missingProjects.Add(absolutePath);
                _consoleService.Warning($"Project found in solution but file missing: {absolutePath}");
            }
        }

        return true;
    }

    /// <summary>
    /// Every project file under a directory, honoring the same exclusions and symlink guard
    /// batch discovery uses — build output and vendored trees are not projects to migrate.
    /// </summary>
    private List<string> DiscoverProjectsInDirectory(string rootPath)
    {
        var projects = new List<string>();
        DiscoverProjectsRecursive(rootPath, projects, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return projects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void DiscoverProjectsRecursive(
        string directory,
        List<string> projects,
        HashSet<string> visitedPaths)
    {
        try
        {
            // The real path is what a symlink loop revisits, not the path that reached it.
            var realPath = Path.GetFullPath(directory);
            if (!visitedPaths.Add(realPath))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.*proj").Where(IsProjectFile))
            {
                projects.Add(Path.GetFullPath(file));
                _consoleService.Info($"Found project: {Path.GetFileNameWithoutExtension(file)}");
            }

            foreach (var subDir in Directory
                .EnumerateDirectories(directory)
                .Where(d => !BatchService.DefaultExcludedDirectories.Contains(Path.GetFileName(d))))
            {
                DiscoverProjectsRecursive(subDir, projects, visitedPaths);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A subtree we cannot read is skipped, not fatal — same rule batch discovery keeps.
        }
    }

    private static bool IsProjectFile(string path)
    {
        var extension = GetSafeExtension(path);
        return extension is ".csproj" or ".fsproj" or ".vbproj";
    }

    private string? PromptForSolutionSelection(string[] slnFiles)
    {
        var choices = slnFiles.Select(f => Path.GetFileName(f) ?? f).ToList();

        // Which solution to migrate is not a guessable default — picking one arbitrarily could
        // rewrite the wrong projects. On a terminal that cannot prompt, name the candidates and
        // make the caller disambiguate with -s instead.
        if (!_consoleService.IsInteractive)
        {
            _consoleService.Error($"Found {slnFiles.Length} solution files and cannot prompt on a non-interactive terminal.");
            foreach (var choice in choices)
            {
                _consoleService.Dim($"  • {choice}");
            }
            _consoleService.Info("Pass the one you want explicitly, e.g. -s ./MySolution.sln");
            return null;
        }

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

