using CPMigrate.Models;
using Microsoft.Build.Construction;

namespace CPMigrate.Services;

/// <summary>
/// Analyzes .NET projects and solutions to extract package references.
/// </summary>
public partial class ProjectAnalyzer
{
    private readonly IConsoleService _consoleService;

    public ProjectAnalyzer(IConsoleService consoleService)
    {
        _consoleService = consoleService;
    }

    /// <summary>
    /// Discovers project paths from a solution file or directory.
    /// </summary>
    /// <param name="solutionPath">Path to solution file or directory containing .sln files.</param>
    /// <returns>Tuple of (base path, list of project file paths).</returns>
    public (string BasePath, List<string> ProjectPaths) DiscoverProjectsFromSolution(string solutionPath)
    {
        var projectPaths = new List<string>();
        var fullPath = Path.GetFullPath(solutionPath);

        if (Directory.Exists(fullPath))
        {
            var slnFiles = Directory.GetFiles(fullPath, "*.sln");
            if (slnFiles.Length == 0)
            {
                _consoleService.Info("No solution file found in the specified directory.");
                return (string.Empty, projectPaths);
            }

            if (slnFiles.Length > 1)
            {
                fullPath = PromptForSolutionSelection(slnFiles);
                // Re-validate selected file exists (defensive - shouldn't fail but protects against race conditions)
                if (!File.Exists(fullPath))
                {
                    _consoleService.Error($"Selected solution file no longer exists: {Path.GetFileName(fullPath)}");
                    return (string.Empty, projectPaths);
                }
            }
            else
            {
                fullPath = slnFiles[0];
            }
        }

        if (!File.Exists(fullPath))
        {
            _consoleService.Info("Solution file not found.");
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
            var solution = SolutionFile.Parse(fullPath);
            
            foreach (var project in solution.ProjectsInOrder)
            {
                if (project.ProjectType == SolutionProjectType.SolutionFolder) continue;

                var extension = Path.GetExtension(project.AbsolutePath).ToLowerInvariant();
                if (extension == ".csproj" || extension == ".fsproj" || extension == ".vbproj")
                {
                    if (File.Exists(project.AbsolutePath))
                    {
                        projectPaths.Add(project.AbsolutePath);
                        _consoleService.Info($"Found project: {project.ProjectName}");
                    }
                    else
                    {
                        _consoleService.Warning($"Project found in solution but file missing: {project.AbsolutePath}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Failed to parse solution file: {ex.Message}");
            throw;
        }

        return (basePath, projectPaths);
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
    /// Processes a project file to extract package references and optionally remove version attributes.
    /// </summary>
    /// <param name="projectFilePath">Full path to the project file.</param>
    /// <param name="packageVersions">Dictionary to accumulate package names to version sets.</param>
    /// <param name="keepVersionAttributes">If true, keeps Version attributes in the project file.</param>
    /// <returns>Modified project file content as a string.</returns>
    public string ProcessProject(string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false)
    {
        // Use Microsoft.Build.Construction to parse the project file as XML
        var projectRoot = ProjectRootElement.Open(projectFilePath);
        bool modified = false;

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
                        value.Add(packageVersion);
                    else
                        packageVersions.Add(packageName, new HashSet<string> { packageVersion });

                    if (!keepVersionAttributes)
                    {
                        // Remove the version metadata
                        versionMetadata.Parent.RemoveChild(versionMetadata);
                        modified = true;
                    }
                }
            }
        }

        return projectRoot.RawXml;
    }

    /// <summary>
    /// Scans a project file to extract all package references without modifying the file.
    /// Used for analysis mode.
    /// </summary>
    /// <param name="projectFilePath">Full path to the project file.</param>
    /// <returns>Tuple of (package references, success status).</returns>
    public (List<PackageReference> References, bool Success) ScanProjectPackages(string projectFilePath)
    {
        var references = new List<PackageReference>();
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var projectRoot = ProjectRootElement.Open(projectFilePath);

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
        catch (Exception ex)
        {
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
}
