using CPMigrate.Models;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace CPMigrate.Services;

/// <summary>
/// Analyzes .NET projects and solutions to extract package references.
/// </summary>
public partial class ProjectAnalyzer : IProjectAnalyzer
{
    private readonly IConsoleService _consoleService;
    private readonly IDotNetCliService _dotNetCliService;
    private readonly ILogger<ProjectAnalyzer> _logger;

    public ProjectAnalyzer(IConsoleService consoleService, IDotNetCliService? dotNetCliService = null, ILogger<ProjectAnalyzer>? logger = null)
    {
        _consoleService = consoleService;
        _dotNetCliService = dotNetCliService ?? new DotNetCliService();
        _logger = logger ?? NullLogger<ProjectAnalyzer>.Instance;
    }

    /// <summary>
    /// Discovers project paths from a solution file or directory.
    /// </summary>
    /// <param name="solutionPath">Path to solution file or directory containing .sln files.</param>
    /// <returns>Tuple of (base path, list of project file paths).</returns>
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
#pragma warning disable S2139 // Exceptions should be either logged or rethrown but not both - console message is user-facing, not logging
            _consoleService.Error($"Failed to parse solution file: {ex.Message}");
            throw;
#pragma warning restore S2139
        }

        return (basePath, projectPaths);
    }

    /// <inheritdoc />
    public (string BasePath, List<string> ProjectPaths) DiscoverProjectsFromSolution(string solutionPath)
    {
        return DiscoverProjectsFromSolutionAsync(solutionPath).GetAwaiter().GetResult();
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
        // Use Microsoft.VisualStudio.SolutionPersistence for unified .sln/.slnx parsing
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

    /// <summary>
    /// Discovers project path from a directory or direct file path.
    /// </summary>
    /// <param name="projectPath">Path to project file or directory containing project files.</param>
    /// <returns>Tuple of (base path, list of project file paths).</returns>
    public (string BasePath, List<string> ProjectPaths) DiscoverProjectFromPath(string projectPath)
    {
        var projectPaths = new List<string>();
        var fullPath = Path.GetFullPath(projectPath);

        if (Directory.Exists(fullPath))
        {
            // Use EnumerateFiles for better performance - stops at first match
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
            return (string.Empty, new List<string>());
        }
        return (basePath, projectPaths);
    }

    /// <summary>
    /// Extracts the target framework from a project file.
    /// </summary>
    public static string GetTargetFramework(string projectFilePath)
    {
        try
        {
            using var projectCollection = new ProjectCollection();
            var projectRoot = ProjectRootElement.Open(projectFilePath, projectCollection);

            var targetFramework = projectRoot.Properties
                .FirstOrDefault(p => p.Name == "TargetFramework" || p.Name == "TargetFrameworks")?.Value ?? "Unknown";

            return targetFramework;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Processes a project file to extract package references and optionally remove version attributes.
    /// </summary>
    /// <param name="projectFilePath">Full path to the project file.</param>
    /// <param name="packageVersions">Dictionary to accumulate package names to version sets.</param>
    /// <param name="keepVersionAttributes">If true, keeps Version attributes in the project file.</param>
    /// <returns>Modified project file content as a string.</returns>
    public static string ProcessProject(string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false)
    {
        // Use a dedicated ProjectCollection to avoid polluting the global cache
        // and ensure proper cleanup of resources
        using var projectCollection = new ProjectCollection();
        var projectRoot = ProjectRootElement.Open(projectFilePath, projectCollection);

        try
        {
            foreach (var item in projectRoot.Items)
            {
                if (item.ItemType == "PackageReference")
                {
                    // Get the Version metadata (attribute or child element)
                    var versionMetadata = item.Metadata.FirstOrDefault(m => m.Name == "Version");

                    if (versionMetadata != null && !string.IsNullOrEmpty(versionMetadata.Value))
                    {
                        var packageName = item.Include;
                        var packageVersion = versionMetadata.Value;

                        if (packageVersions.TryGetValue(packageName, out var value))
                        {
                            value.Add(packageVersion);
                        }
                        else
                        {
                            packageVersions.Add(packageName, new HashSet<string> { packageVersion });
                        }

                        if (!keepVersionAttributes)
                        {
                            // Remove the version metadata
                            versionMetadata.Parent.RemoveChild(versionMetadata);
                        }
                    }
                }
            }

            return projectRoot.RawXml;
        }
        finally
        {
            // Explicitly unload the project to release resources
            projectCollection.UnloadAllProjects();
        }
    }

    /// <summary>
    /// Scans a project file and populates a package versions dictionary.
    /// </summary>
    public bool ScanProjectPackages(string projectFilePath, Dictionary<string, HashSet<string>> packageVersions)
    {
        var (refs, success) = ScanProjectPackages(projectFilePath);
        if (!success)
        {
            return false;
        }

        foreach (var r in refs)
        {
            if (packageVersions.TryGetValue(r.PackageName, out var versions))
            {
                versions.Add(r.Version);
            }
            else
            {
                packageVersions.Add(r.PackageName, new HashSet<string> { r.Version });
            }
        }

        return true;
    }

    /// <summary>
    /// Scans a project file to extract all package references without modifying the file.
    /// Used for analysis mode.
    /// </summary>
    public (List<PackageReference> References, bool Success) ScanProjectPackages(string projectFilePath)
    {
        var references = new List<PackageReference>();
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            // Use a dedicated ProjectCollection to avoid polluting the global cache
            using var projectCollection = new ProjectCollection();
            var projectRoot = ProjectRootElement.Open(projectFilePath, projectCollection);

            try
            {
                foreach (var item in projectRoot.Items)
                {
                    if (item.ItemType == "PackageReference")
                    {
                        var versionMetadata = item.Metadata.FirstOrDefault(m => m.Name == "Version");
                        if (versionMetadata != null && !string.IsNullOrEmpty(versionMetadata.Value))
                        {
                            references.Add(new PackageReference(
                                item.Include,
                                versionMetadata.Value,
                                projectFilePath,
                                projectName
                            ));
                        }
                    }
                }

                return (references, true);
            }
            finally
            {
                projectCollection.UnloadAllProjects();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not scan project: {ProjectName}", projectName);
            _consoleService.Warning($"Could not scan {projectName}: {ex.Message}");
            return (references, false);
        }
    }

    /// <summary>
    /// Prompts the user to select a solution file when multiple are found.
    /// </summary>
    private string PromptForSolutionSelection(string[] slnFiles)
    {
        var choices = slnFiles.Select(f => Path.GetFileName(f) ?? f).ToList();

        var selection = _consoleService.AskSelection(
            "Multiple solution files found. Which one would you like to use?",
            choices);

        return slnFiles.First(f => Path.GetFileName(f) == selection);
    }

    /// <summary>
    /// Gets all solution files (.sln and .slnx) from a directory.
    /// </summary>
    /// <param name="directory">Directory to search.</param>
    /// <param name="searchOption">Search option (default: TopDirectoryOnly).</param>
    /// <returns>Array of solution file paths.</returns>
    public static string[] GetSolutionFiles(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.GetFiles(directory, "*.sln", searchOption)
            .Concat(Directory.GetFiles(directory, "*.slnx", searchOption))
            .ToArray();
    }
}
