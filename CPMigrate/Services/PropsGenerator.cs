using System.Security;
using System.Text;
using Microsoft.Build.Construction;

namespace CPMigrate.Services;

/// <summary>
/// Generates Directory.Packages.props content from collected package versions.
/// </summary>
public class PropsGenerator
{
    private const string PackageVersionItemType = "PackageVersion";
    private const string VersionMetadataName = "Version";

    private readonly VersionResolver _versionResolver;

    public PropsGenerator(VersionResolver? versionResolver = null)
    {
        _versionResolver = versionResolver ?? new VersionResolver();
    }

    /// <summary>
    /// Generates the Directory.Packages.props XML content from collected package versions.
    /// Resolves version conflicts based on the specified strategy.
    /// </summary>
    /// <param name="packageVersions">Dictionary mapping package names to their version sets.</param>
    /// <param name="strategy">Strategy for resolving version conflicts.</param>
    /// <returns>Complete XML content for Directory.Packages.props file.</returns>
    public string Generate(Dictionary<string, HashSet<string>> packageVersions,
        ConflictStrategy strategy = ConflictStrategy.Highest)
    {
        var header = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                """;

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine(header);

        foreach (var kvp in packageVersions.OrderBy(x => x.Key))
        {
            // Skip packages with no versions (shouldn't happen, but defensive)
            if (kvp.Value.Count == 0)
            {
                continue;
            }

            // Resolve to single version if multiple exist
            var version = kvp.Value.Count > 1
                ? _versionResolver.ResolveVersion(kvp.Value, strategy)
                : kvp.Value.First();

            // XML-encode package name and version to prevent XML injection
            var safePackageName = SecurityElement.Escape(kvp.Key) ?? kvp.Key;
            var safeVersion = SecurityElement.Escape(version) ?? version;
            stringBuilder.AppendLine($"""    <PackageVersion Include="{safePackageName}" Version="{safeVersion}" />""");
        }

        stringBuilder.AppendLine("""
                                   </ItemGroup>
                                 </Project>
                                 """);
        return stringBuilder.ToString();
    }

    public static Dictionary<string, HashSet<string>> ReadExistingPackageVersions(
        string propsFilePath,
        out bool hasConditionalPackageVersions)
    {
        hasConditionalPackageVersions = false;
        var packageVersions = new Dictionary<string, HashSet<string>>();
        if (!File.Exists(propsFilePath))
        {
            throw new FileNotFoundException($"Props file not found: {propsFilePath}", propsFilePath);
        }
        var projectRoot = ProjectRootElement.Open(propsFilePath);

        foreach (var item in projectRoot.Items.Where(i => i.ItemType == PackageVersionItemType))
        {
            if (!string.IsNullOrEmpty(item.Condition) || !string.IsNullOrEmpty(item.Parent?.Condition))
            {
                hasConditionalPackageVersions = true;
            }

            var packageName = !string.IsNullOrWhiteSpace(item.Include) ? item.Include : item.Update;
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            var version = GetMetadataValue(item, VersionMetadataName);
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (!packageVersions.TryGetValue(packageName, out var versions))
            {
                versions = new HashSet<string>();
                packageVersions.Add(packageName, versions);
            }

            versions.Add(version);
        }

        return packageVersions;
    }

    public (string Content, int AddedCount, int UpdatedCount, bool HasConditionalPackageVersions) MergeExisting(
        string propsFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        ConflictStrategy strategy = ConflictStrategy.Highest)
    {
        if (!File.Exists(propsFilePath))
        {
            throw new FileNotFoundException($"Props file not found: {propsFilePath}", propsFilePath);
        }

        var projectRoot = ProjectRootElement.Open(propsFilePath);
        var (itemsByPackage, hasConditionalPackageVersions) = BuildExistingItemsMap(projectRoot);

        EnsureManagePackageVersionsCentrally(projectRoot);

        var targetItemGroup = GetOrCreateTargetItemGroup(projectRoot);
        var (addedCount, updatedCount) = ProcessPackageVersions(
            packageVersions,
            strategy,
            itemsByPackage,
            targetItemGroup);

        return (projectRoot.RawXml, addedCount, updatedCount, hasConditionalPackageVersions);
    }

    private static (Dictionary<string, List<ProjectItemElement>> ItemsByPackage, bool HasConditionalVersions)
        BuildExistingItemsMap(ProjectRootElement projectRoot)
    {
        var itemsByPackage = new Dictionary<string, List<ProjectItemElement>>();
        var hasConditionalPackageVersions = false;

        foreach (var item in projectRoot.Items.Where(i => i.ItemType == PackageVersionItemType))
        {
            if (!string.IsNullOrEmpty(item.Condition) || !string.IsNullOrEmpty(item.Parent?.Condition))
            {
                hasConditionalPackageVersions = true;
            }

            var packageName = GetPackageName(item);
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            AddToItemsMap(itemsByPackage, packageName, item);
        }

        return (itemsByPackage, hasConditionalPackageVersions);
    }

    private static string GetPackageName(ProjectItemElement item)
    {
        return !string.IsNullOrWhiteSpace(item.Include) ? item.Include : item.Update;
    }

    private static void AddToItemsMap(
        Dictionary<string, List<ProjectItemElement>> itemsByPackage,
        string packageName,
        ProjectItemElement item)
    {
        if (!itemsByPackage.TryGetValue(packageName, out var items))
        {
            items = new List<ProjectItemElement>();
            itemsByPackage.Add(packageName, items);
        }

        items.Add(item);
    }

    private static ProjectItemGroupElement GetOrCreateTargetItemGroup(ProjectRootElement projectRoot)
    {
        return projectRoot.ItemGroups
            .FirstOrDefault(group => string.IsNullOrEmpty(group.Condition)
                && group.Items.Any(item => item.ItemType == PackageVersionItemType))
            ?? projectRoot.AddItemGroup();
    }

    private (int AddedCount, int UpdatedCount) ProcessPackageVersions(
        Dictionary<string, HashSet<string>> packageVersions,
        ConflictStrategy strategy,
        Dictionary<string, List<ProjectItemElement>> itemsByPackage,
        ProjectItemGroupElement targetItemGroup)
    {
        var addedCount = 0;
        var updatedCount = 0;

        foreach (var kvp in packageVersions.OrderBy(k => k.Key))
        {
            if (kvp.Value.Count == 0)
            {
                continue;
            }

            var resolvedVersion = ResolvePackageVersion(kvp.Value, strategy);

            if (itemsByPackage.TryGetValue(kvp.Key, out var existingItems))
            {
                // Graceful refinement for conditional versions:
                // If there are multiple existing items (conditional) and they already cover 
                // all versions found (including existing ones), don't flatten them.
                if (existingItems.Count > 1)
                {
                    var existingVersions = existingItems
                        .Select(item => GetMetadataValue(item, VersionMetadataName))
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Select(v => v!)
                        .ToHashSet();

                    if (kvp.Value.IsSubsetOf(existingVersions))
                    {
                        continue;
                    }
                }

                if (UpdateExistingItems(existingItems, resolvedVersion))
                {
                    updatedCount++;
                }
            }
            else
            {
                AddNewPackageVersion(targetItemGroup, kvp.Key, resolvedVersion);
                addedCount++;
            }
        }

        return (addedCount, updatedCount);
    }

    private string ResolvePackageVersion(HashSet<string> versions, ConflictStrategy strategy)
    {
        return versions.Count > 1
            ? _versionResolver.ResolveVersion(versions, strategy)
            : versions.First();
    }

    private static bool UpdateExistingItems(List<ProjectItemElement> items, string resolvedVersion)
    {
        var updated = false;

        foreach (var item in items)
        {
            var currentVersion = GetMetadataValue(item, VersionMetadataName);
            if (!string.Equals(currentVersion, resolvedVersion, StringComparison.OrdinalIgnoreCase))
            {
                SetMetadataValue(item, VersionMetadataName, resolvedVersion);
                updated = true;
            }
        }

        return updated;
    }

    private static void AddNewPackageVersion(
        ProjectItemGroupElement targetItemGroup,
        string packageName,
        string version)
    {
        var newItem = targetItemGroup.AddItem(PackageVersionItemType, packageName);
        SetMetadataValue(newItem, VersionMetadataName, version);
    }

    private static void EnsureManagePackageVersionsCentrally(ProjectRootElement projectRoot)
    {
        var hasProperty = projectRoot.Properties.Any(p => p.Name == "ManagePackageVersionsCentrally");
        if (hasProperty)
        {
            return;
        }

        var propertyGroup = projectRoot.PropertyGroups
            .FirstOrDefault(group => string.IsNullOrEmpty(group.Condition))
            ?? projectRoot.AddPropertyGroup();

        propertyGroup.AddProperty("ManagePackageVersionsCentrally", "true");
    }

    private static string? GetMetadataValue(ProjectItemElement item, string name)
    {
        var metadata = item.Metadata
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return metadata?.Value;
    }

    private static void SetMetadataValue(ProjectItemElement item, string name, string value)
    {
        var metadata = item.Metadata
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (metadata != null)
        {
            metadata.Value = value;
        }
        else
        {
            item.AddMetadata(name, value);
        }
    }
}
