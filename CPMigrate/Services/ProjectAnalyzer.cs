using System.Text.RegularExpressions;
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

        var basePath = Path.GetDirectoryName(fullPath)!;
        var solutionContent = File.ReadAllText(fullPath);
        var matches = ProjectRegex().Matches(solutionContent);

        foreach (Match match in matches)
        {
            var projectPath = match.Groups[3].Value;
            if (!projectPath.EndsWith("csproj")) continue;

            // Normalize path separators for cross-platform compatibility
            projectPath = projectPath.Replace('\\', Path.DirectorySeparatorChar);
            var projectFilePath = Path.GetFullPath(Path.Combine(basePath, projectPath));
            projectPaths.Add(projectFilePath);
            _consoleService.Info($"Found project: {match.Groups[2].Value}");
        }

        return (basePath, projectPaths);
    }

    /// <summary>
    /// Discovers project path from a directory or direct file path.
    /// </summary>
    /// <param name="projectPath">Path to project file or directory containing .csproj files.</param>
    /// <returns>Tuple of (base path, list of project file paths).</returns>
    public (string BasePath, List<string> ProjectPaths) DiscoverProjectFromPath(string projectPath)
    {
        var projectPaths = new List<string>();
        var fullPath = Path.GetFullPath(projectPath);

        if (Directory.Exists(fullPath))
        {
            var projFiles = Directory.GetFiles(fullPath, "*.csproj");
            if (projFiles.Length == 0)
            {
                _consoleService.Info("No project file found in the specified directory.");
                return (string.Empty, projectPaths);
            }
            fullPath = projFiles[0];
        }

        if (!File.Exists(fullPath))
        {
            _consoleService.Info("Project file not found.");
            return (string.Empty, projectPaths);
        }

        projectPaths.Add(fullPath);
        var basePath = Path.GetDirectoryName(fullPath)!;
        return (basePath, projectPaths);
    }

    /// <summary>
    /// Processes a project file to extract package references and optionally remove version attributes.
    /// </summary>
    /// <param name="projectFilePath">Full path to the .csproj file.</param>
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
    /// Regex to find project files with their path and project name from .sln files.
    /// </summary>
    [GeneratedRegex(@"Project\(""\{(.+?)\}""\) = ""(.+?)"", ""(.+?)""", RegexOptions.Multiline)]
    private static partial Regex ProjectRegex();
}