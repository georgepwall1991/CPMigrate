using System.Text;
using System.Text.RegularExpressions;
using Buildalyzer;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Analyzes .NET projects and solutions to extract package references.
/// </summary>
public partial class ProjectAnalyzer
{
    private readonly AnalyzerManager _analyzerManager;

    public ProjectAnalyzer(AnalyzerManager? analyzerManager = null)
    {
        _analyzerManager = analyzerManager ?? new AnalyzerManager();
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
                ConsoleOutput.Info("No solution file found in the specified directory.");
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
            ConsoleOutput.Info("Solution file not found.");
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
            ConsoleOutput.Info($"Found project: {match.Groups[2].Value}");
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
                ConsoleOutput.Info("No project file found in the specified directory.");
                return (string.Empty, projectPaths);
            }
            fullPath = projFiles[0];
        }

        if (!File.Exists(fullPath))
        {
            ConsoleOutput.Info("Project file not found.");
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
        var analyzer = _analyzerManager.GetProject(projectFilePath);
        var stringBuilder = new StringBuilder(File.ReadAllText(projectFilePath));

        foreach (var reference in analyzer.ProjectFile.PackageReferences)
        {
            if (packageVersions.TryGetValue(reference.Name, out var value))
                value.Add(reference.Version);
            else
                packageVersions.Add(reference.Name, new HashSet<string> { reference.Version });

            if (!keepVersionAttributes)
            {
                stringBuilder.Replace($"Version=\"{reference.Version}\"", "");
            }
        }

        return stringBuilder.ToString();
    }

    /// <summary>
    /// Prompts the user to select a solution file when multiple are found.
    /// </summary>
    private static string PromptForSolutionSelection(string[] slnFiles)
    {
        var choices = slnFiles.Select(f => Path.GetFileName(f) ?? f).ToList();

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Multiple solution files found. Which one would you like to use?[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(choices));

        return slnFiles.First(f => Path.GetFileName(f) == selection);
    }

    /// <summary>
    /// Regex to find project files with their path and project name from .sln files.
    /// </summary>
    [GeneratedRegex(@"Project\(""\{(.+?)\}""\) = ""(.+?)"", ""(.+?)""", RegexOptions.Multiline)]
    private static partial Regex ProjectRegex();
}
