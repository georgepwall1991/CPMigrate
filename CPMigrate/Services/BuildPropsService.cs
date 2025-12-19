using System.Text;
using System.Xml.Linq;
using CPMigrate.Models;
using Microsoft.Build.Construction;

namespace CPMigrate.Services;

public class BuildPropsService
{
    private readonly IConsoleService _consoleService;
    private readonly BuildPropsAnalyzer _analyzer;
    private readonly ProjectAnalyzer _projectAnalyzer;

    public BuildPropsService(IConsoleService consoleService, ProjectAnalyzer projectAnalyzer)
    {
        _consoleService = consoleService;
        _projectAnalyzer = projectAnalyzer;
        _analyzer = new BuildPropsAnalyzer(consoleService);
    }

    public async Task<int> UnifyPropertiesAsync(Options options)
    {
        var startDir = !string.IsNullOrEmpty(options.SolutionFileDir) ? options.SolutionFileDir : ".";
        var (basePath, projectPaths) = _projectAnalyzer.DiscoverProjectsFromSolution(startDir);

        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to analyze.");
            return ExitCodes.UnexpectedError;
        }

        _consoleService.Banner("Analyzing Project Properties...");
        var analysis = _analyzer.Analyze(projectPaths);

        // Filter for properties that are present in ALL projects with the SAME value
        // Key format is "Name|Value"
        var candidates = analysis.PropertyOccurrences
            .Where(kv => kv.Value.Count == analysis.TotalProjects)
            .Select(kv => kv.Value.First()) // Take one representative
            .OrderBy(p => p.Name)
            .ToList();

        if (candidates.Count == 0)
        {
            _consoleService.Info("No common properties found across all projects.");
            return ExitCodes.Success;
        }

        _consoleService.Info($"Found {candidates.Count} common properties across {analysis.TotalProjects} projects:");
        foreach (var prop in candidates)
        {
            _consoleService.Dim($"  - {prop.Name} = {prop.Value}");
        }

        if (options.DryRun)
        {
            _consoleService.DryRun("Would create/update Directory.Build.props with these properties.");
            _consoleService.DryRun("Would remove these properties from all project files.");
            return ExitCodes.Success;
        }

        if (!options.Force && !_consoleService.AskConfirmation("Do you want to move these properties to Directory.Build.props?"))
        {
            return ExitCodes.Success;
        }

        var buildPropsPath = Path.Combine(basePath, "Directory.Build.props");
        await CreateOrUpdateBuildProps(buildPropsPath, candidates);
        await RemovePropertiesFromProjects(projectPaths, candidates);

        _consoleService.Success($"Successfully moved {candidates.Count} properties to Directory.Build.props");
        return ExitCodes.Success;
    }

    private async Task CreateOrUpdateBuildProps(string path, List<ProjectProperty> properties)
    {
        ProjectRootElement root;
        if (File.Exists(path))
        {
            _consoleService.Info($"Updating existing {Path.GetFileName(path)}...");
            root = ProjectRootElement.Open(path);
        }
        else
        {
            _consoleService.Info($"Creating new {Path.GetFileName(path)}...");
            root = ProjectRootElement.Create();
        }

        var propertyGroup = root.PropertyGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition));
        if (propertyGroup == null)
        {
            propertyGroup = root.AddPropertyGroup();
        }

        foreach (var prop in properties)
        {
            // Check if exists
            var existing = propertyGroup.Properties.FirstOrDefault(p => p.Name == prop.Name);
            if (existing != null)
            {
                existing.Value = prop.Value;
            }
            else
            {
                propertyGroup.AddProperty(prop.Name, prop.Value);
            }
        }

        root.Save(path);
    }

    private async Task RemovePropertiesFromProjects(List<string> projectPaths, List<ProjectProperty> propertiesToRemove)
    {
        var propertiesSet = new HashSet<string>(propertiesToRemove.Select(p => p.Name));

        foreach (var projectPath in projectPaths)
        {
            var root = ProjectRootElement.Open(projectPath);
            var modified = false;

            foreach (var group in root.PropertyGroups)
            {
                // ToList to allow modification during iteration
                var props = group.Properties.Where(p => propertiesSet.Contains(p.Name)).ToList();
                foreach (var prop in props)
                {
                    // Only remove if value matches (defensive, though our analysis said they all match)
                    // Actually, if we are moving to Directory.Build.props, we can remove regardless of value 
                    // IF we want to enforce the central value. But our analysis ensured they are identical.
                    var targetValue = propertiesToRemove.First(p => p.Name == prop.Name).Value;
                    if (prop.Value == targetValue)
                    {
                        group.RemoveChild(prop);
                        modified = true;
                    }
                }
            }

            // Remove empty property groups
            var emptyGroups = root.PropertyGroups.Where(g => g.Count == 0 && string.IsNullOrEmpty(g.Condition)).ToList();
            foreach (var group in emptyGroups)
            {
                root.RemoveChild(group);
                modified = true;
            }

            if (modified)
            {
                root.Save(projectPath);
                _consoleService.Dim($"Updated {Path.GetFileName(projectPath)}");
            }
        }
    }
}
