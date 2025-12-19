using System.Security;
using System.Text;
using Microsoft.Build.Construction;

namespace CPMigrate.Services;

/// <summary>
/// Generates Directory.Packages.props content from collected package versions.
/// </summary>
public class PropsGenerator
{
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
                continue;

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

    public Dictionary<string, HashSet<string>> ReadExistingPackageVersions(
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

        foreach (var item in projectRoot.Items.Where(i => i.ItemType == "PackageVersion"))
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

            var version = GetMetadataValue(item, "Version");
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
        var itemsByPackage = new Dictionary<string, List<ProjectItemElement>>();
        var hasConditionalPackageVersions = false;

        foreach (var item in projectRoot.Items.Where(i => i.ItemType == "PackageVersion"))
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

            if (!itemsByPackage.TryGetValue(packageName, out var items))
            {
                items = new List<ProjectItemElement>();
                itemsByPackage.Add(packageName, items);
            }

            items.Add(item);
        }

        EnsureManagePackageVersionsCentrally(projectRoot);

        var targetItemGroup = projectRoot.ItemGroups
            .FirstOrDefault(group => string.IsNullOrEmpty(group.Condition)
                && group.Items.Any(item => item.ItemType == "PackageVersion"))
            ?? projectRoot.AddItemGroup();

        var addedCount = 0;
        var updatedCount = 0;

        foreach (var kvp in packageVersions.OrderBy(k => k.Key))
        {
            if (kvp.Value.Count == 0)
            {
                continue;
            }

            var resolvedVersion = kvp.Value.Count > 1
                ? _versionResolver.ResolveVersion(kvp.Value, strategy)
                : kvp.Value.First();

            if (itemsByPackage.TryGetValue(kvp.Key, out var existingItems))
            {
                var updated = false;
                foreach (var item in existingItems)
                {
                    var currentVersion = GetMetadataValue(item, "Version");
                    if (!string.Equals(currentVersion, resolvedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        SetMetadataValue(item, "Version", resolvedVersion);
                        updated = true;
                    }
                }

                if (updated)
                {
                    updatedCount++;
                }
            }
            else
            {
                var newItem = targetItemGroup.AddItem("PackageVersion", kvp.Key);
                SetMetadataValue(newItem, "Version", resolvedVersion);
                addedCount++;
            }
        }

        return (projectRoot.RawXml, addedCount, updatedCount, hasConditionalPackageVersions);
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
