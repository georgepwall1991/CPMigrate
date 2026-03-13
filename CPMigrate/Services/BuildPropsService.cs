using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace CPMigrate.Services;

public class BuildPropsService
{
    /// <summary>
    /// Minimum percentage of projects that must share the same property value
    /// for it to be considered a unification candidate.
    /// </summary>
    private const double ConsensusThresholdPercent = 0.6;

    private readonly IConsoleService _consoleService;
    private readonly BuildPropsAnalyzer _analyzer;
    private readonly IProjectAnalyzer _projectAnalyzer;

    public BuildPropsService(IConsoleService consoleService, IProjectAnalyzer projectAnalyzer)
    {
        _consoleService = consoleService;
        _projectAnalyzer = projectAnalyzer;
        _analyzer = new BuildPropsAnalyzer(consoleService);
    }

    public async Task<int> UnifyPropertiesAsync(Options options)
    {
        var startDir = !string.IsNullOrEmpty(options.SolutionFileDir) ? options.SolutionFileDir : ".";
        var (basePath, projectPaths) = await _projectAnalyzer.DiscoverProjectsFromSolutionAsync(startDir);

        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to analyze.");
            return ExitCodes.UnexpectedError;
        }

        _consoleService.Banner("Analyzing Project Properties...");
        var analysis = _analyzer.Analyze(projectPaths);

        // Filter for properties that are present in at least the consensus threshold of projects with the SAME value
        var threshold = Math.Ceiling(analysis.TotalProjects * ConsensusThresholdPercent);

        // --- PROPERTIES ---
        var propertyCandidates = analysis.PropertyOccurrences
            .GroupBy(kv => kv.Value[0].Name)
            .Select(g =>
            {
                var mostCommon = g.MaxBy(kv => kv.Value.Count);
                return new
                {
                    Property = mostCommon!.Value[0],
                    Count = mostCommon.Value.Count
                };
            })
            .Where(x => x.Count >= threshold)
            .OrderBy(x => x.Property.Name)
            .ToList();

        // --- ITEMS (Using, PackageReference) ---
        // Key format: Type|Include|MetadataString
        var itemCandidates = analysis.ItemOccurrences
            .GroupBy(kv => $"{kv.Value[0].ItemType}|{kv.Value[0].Include}") // Group by Type+Include
            .Select(g =>
            {
                var mostCommon = g.MaxBy(kv => kv.Value.Count); // Find specific metadata set with highest count
                return new
                {
                    Item = mostCommon!.Value[0],
                    Count = mostCommon.Value.Count
                };
            })
            .Where(x => x.Count >= threshold)
            .OrderBy(x => x.Item.ItemType).ThenBy(x => x.Item.Include)
            .ToList();

        if (propertyCandidates.Count == 0 && itemCandidates.Count == 0)
        {
            _consoleService.Info($"No common properties or items found (checked for >{ConsensusThresholdPercent:P0} consensus).");
            return ExitCodes.Success;
        }

        if (propertyCandidates.Count > 0)
        {
            _consoleService.Info($"Found {propertyCandidates.Count} common properties (consensus > {ConsensusThresholdPercent:P0}):");
            foreach (var candidate in propertyCandidates)
            {
                var percentage = (double)candidate.Count / analysis.TotalProjects * 100;
                _consoleService.Dim($"  - {candidate.Property.Name} = {candidate.Property.Value} [green]({candidate.Count}/{analysis.TotalProjects}, {percentage:F0}%)[/]");
            }
        }

        if (itemCandidates.Count > 0)
        {
            _consoleService.Info($"Found {itemCandidates.Count} common items (consensus > {ConsensusThresholdPercent:P0}):");
            foreach (var candidate in itemCandidates)
            {
                var percentage = (double)candidate.Count / analysis.TotalProjects * 100;
                var meta = candidate.Item.Metadata != null && candidate.Item.Metadata.Count > 0
                    ? $" ({string.Join(", ", candidate.Item.Metadata.Select(m => $"{m.Key}={m.Value}"))})"
                    : "";
                _consoleService.Dim($"  - [{candidate.Item.ItemType}] {candidate.Item.Include}{meta} [green]({candidate.Count}/{analysis.TotalProjects}, {percentage:F0}%)[/]");
            }
        }

        if (options.DryRun)
        {
            _consoleService.DryRun("Would create/update Directory.Build.props with these items.");
            _consoleService.DryRun("Would remove these items from matching project files.");
            return ExitCodes.Success;
        }

        if (!options.Force && !_consoleService.AskConfirmation("Do you want to move these to Directory.Build.props?"))
        {
            return ExitCodes.Success;
        }

        var propsList = propertyCandidates.Select(c => c.Property).ToList();
        var itemsList = itemCandidates.Select(c => c.Item).ToList();
        var buildPropsPath = Path.Combine(basePath, "Directory.Build.props");

        await CreateOrUpdateBuildProps(buildPropsPath, propsList, itemsList);
        await RemovePropertiesFromProjects(projectPaths, propsList);
        await RemoveItemsFromProjects(projectPaths, itemsList);

        _consoleService.Success($"Successfully unified {propertyCandidates.Count} properties and {itemCandidates.Count} items.");
        return ExitCodes.Success;
    }

    private async Task CreateOrUpdateBuildProps(string path,
        List<CPMigrate.Models.ProjectProperty> properties,
        List<CPMigrate.Models.ProjectItem> items)
    {
        using var collection = new ProjectCollection();
        ProjectRootElement root;
        if (File.Exists(path))
        {
            _consoleService.Info($"Updating existing {Path.GetFileName(path)}...");
            root = ProjectRootElement.Open(path, collection);
        }
        else
        {
            _consoleService.Info($"Creating new {Path.GetFileName(path)}...");
            root = ProjectRootElement.Create(collection);
        }

        // Add Properties
        if (properties.Count > 0)
        {
            var propertyGroup = root.PropertyGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition));
            if (propertyGroup == null)
            {
                propertyGroup = root.AddPropertyGroup();
            }

            foreach (var prop in properties)
            {
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
        }

        // Add Items
        if (items.Count > 0)
        {
            var itemGroup = root.ItemGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition));
            if (itemGroup == null)
            {
                itemGroup = root.AddItemGroup();
            }

            foreach (var item in items)
            {
                // Check if exists (simplified check by Include)
                var existing = itemGroup.Items.FirstOrDefault(i => i.ItemType == item.ItemType && i.Include == item.Include);
                if (existing != null)
                {
                    // Remove existing to refresh metadata
                    itemGroup.RemoveChild(existing);
                }

                var newItem = itemGroup.AddItem(item.ItemType, item.Include);
                if (item.Metadata != null)
                {
                    foreach (var m in item.Metadata)
                    {
                        newItem.AddMetadata(m.Key, m.Value);
                    }
                }
            }
        }

        root.Save(path);
    }

    private async Task RemoveItemsFromProjects(List<string> projectPaths, List<CPMigrate.Models.ProjectItem> itemsToRemove)
    {
        if (itemsToRemove.Count == 0)
        {
            return;
        }

        // Lookup: Type|Include -> Metadata
        var targetItems = itemsToRemove.ToDictionary(
            i => $"{i.ItemType}|{i.Include}",
            i => i.Metadata
        );

        foreach (var projectPath in projectPaths)
        {
            var modified = ProcessProjectForItemRemoval(projectPath, targetItems);

            if (modified)
            {
                _consoleService.Dim($"Updated {Path.GetFileName(projectPath)}");
            }
        }
    }

    private bool ProcessProjectForItemRemoval(
        string projectPath,
        Dictionary<string, Dictionary<string, string>?> targetItems)
    {
        using var collection = new ProjectCollection();
        var root = ProjectRootElement.Open(projectPath, collection);
        var modified = false;

        foreach (var group in root.ItemGroups)
        {
            var items = group.Items
                .Where(i => targetItems.ContainsKey($"{i.ItemType}|{i.Include}"))
                .ToList();

            foreach (var item in items)
            {
                modified = TryRemoveItemIfMatches(item, targetItems, group, projectPath) || modified;
            }
        }

        // Remove empty item groups
        modified = RemoveEmptyItemGroups(root) || modified;

        if (modified)
        {
            root.Save(projectPath);
        }

        return modified;
    }

    private bool TryRemoveItemIfMatches(
        ProjectItemElement item,
        Dictionary<string, Dictionary<string, string>?> targetItems,
        ProjectItemGroupElement group,
        string projectPath)
    {
        var key = $"{item.ItemType}|{item.Include}";
        var targetMetadata = targetItems[key];
        var itemMetadata = item.Metadata.ToDictionary(m => m.Name, m => m.Value);

        if (!MetadataMatches(itemMetadata, targetMetadata))
        {
            _consoleService.Warning(
                $"Skipped removing item '{item.ItemType} {item.Include}' in {Path.GetFileName(projectPath)}: Metadata mismatch.");
            return false;
        }

        group.RemoveChild(item);
        return true;
    }

    private static bool MetadataMatches(
        Dictionary<string, string> itemMetadata,
        Dictionary<string, string>? targetMetadata)
    {
        // If target has no metadata, item must also have none
        if (targetMetadata == null)
        {
            return itemMetadata.Count == 0;
        }

        // Count must match
        if (itemMetadata.Count != targetMetadata.Count)
        {
            return false;
        }

        // All target metadata must exist with matching values
        return targetMetadata.All(tm =>
            itemMetadata.TryGetValue(tm.Key, out var val) && val == tm.Value);
    }

    private static bool RemoveEmptyItemGroups(ProjectRootElement root)
    {
        var emptyGroups = root.ItemGroups
            .Where(g => g.Count == 0 && string.IsNullOrEmpty(g.Condition))
            .ToList();

        foreach (var group in emptyGroups)
        {
            root.RemoveChild(group);
        }

        return emptyGroups.Count > 0;
    }

    private async Task RemovePropertiesFromProjects(List<string> projectPaths, List<CPMigrate.Models.ProjectProperty> propertiesToRemove)
    {
        if (propertiesToRemove.Count == 0)
        {
            return;
        }

        var propertiesSet = new HashSet<string>(propertiesToRemove.Select(p => p.Name));

        foreach (var projectPath in projectPaths)
        {
            // Use a local collection to ensure no caching issues
            using var collection = new ProjectCollection();
            var root = ProjectRootElement.Open(projectPath, collection);
            var modified = false;

            foreach (var group in root.PropertyGroups)
            {
                // ToList to allow modification during iteration
                var props = group.Properties.Where(p => propertiesSet.Contains(p.Name)).ToList();
                foreach (var prop in props)
                {
                    // Only remove if value matches (defensive, though our analysis said they all match)
                    var targetValue = propertiesToRemove.First(p => p.Name == prop.Name).Value;
                    if (prop.Value == targetValue)
                    {
                        group.RemoveChild(prop);
                        modified = true;
                    }
                    else
                    {
                        // Explicitly log why we aren't removing it, to help the user debug
                        _consoleService.Warning($"Skipped removing '{prop.Name}' in {Path.GetFileName(projectPath)}: Value mismatch.");
                        _consoleService.Dim($"  Expected: '{targetValue}'");
                        _consoleService.Dim($"  Found:    '{prop.Value}'");
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
