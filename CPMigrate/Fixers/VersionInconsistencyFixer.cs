using System.Xml.Linq;
using CPMigrate.Models;

namespace CPMigrate.Fixers;

/// <summary>
/// Fixes version inconsistencies by standardizing package versions across projects
/// using the configured conflict resolution strategy.
/// </summary>
public class VersionInconsistencyFixer : IFixer
{
    public string Name => "Version Inconsistency Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        // This fixer handles issues where a package has multiple different versions
        // The description typically contains version info like "13.0.1 (Project1), 12.0.3 (Project2)"
        return issue.Description.Contains("(") && issue.Description.Contains(",");
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        // Find all references for this package
        var references = packageInfo.References
            .Where(r => r.PackageName.Equals(issue.PackageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (references.Count == 0)
        {
            return FixResult.NoFixNeeded($"No references found for {issue.PackageName}");
        }

        // Determine target version based on conflict strategy
        var versions = references.Select(r => r.Version).Distinct().ToList();
        if (versions.Count <= 1)
        {
            return FixResult.NoFixNeeded($"No version conflict for {issue.PackageName}");
        }

        var targetVersion = ResolveVersion(versions, options.ConflictStrategy);
        if (targetVersion == null)
        {
            return FixResult.Failed($"Cannot resolve version for {issue.PackageName} with Fail strategy");
        }

        var changes = new List<FileChange>();

        // Group by project file to process each file once
        var projectGroups = references
            .Where(r => r.Version != targetVersion)
            .GroupBy(r => r.ProjectPath);

        foreach (var group in projectGroups)
        {
            var projectPath = group.Key;
            var result = UpdateProjectVersions(projectPath, issue.PackageName, targetVersion, dryRun);
            if (result != null)
            {
                changes.Add(result);
            }
        }

        if (changes.Count == 0)
        {
            return FixResult.NoFixNeeded($"All references already at {targetVersion}");
        }

        return FixResult.Succeeded(
            $"Standardized {issue.PackageName} to version {targetVersion} in {changes.Count} project(s)",
            changes
        );
    }

    private static string? ResolveVersion(List<string> versions, ConflictStrategy strategy)
    {
        return strategy switch
        {
            ConflictStrategy.Highest => versions.OrderByDescending(v => ParseVersion(v)).First(),
            ConflictStrategy.Lowest => versions.OrderBy(v => ParseVersion(v)).First(),
            ConflictStrategy.Fail => null,
            _ => versions.First()
        };
    }

    private static Version ParseVersion(string versionString)
    {
        // Handle versions like "1.0.0", "1.0.0-preview", etc.
        var cleanVersion = versionString.Split('-')[0];
        if (Version.TryParse(cleanVersion, out var version))
        {
            return version;
        }
        return new Version(0, 0, 0);
    }

    private static FileChange? UpdateProjectVersions(string projectPath, string packageName, string targetVersion, bool dryRun)
    {
        if (!File.Exists(projectPath))
        {
            return null;
        }

        try
        {
            var originalContent = File.ReadAllText(projectPath);
            var doc = XDocument.Parse(originalContent);

            var packageRefs = doc.Descendants("PackageReference")
                .Where(e => e.Attribute("Include")?.Value
                    .Equals(packageName, StringComparison.OrdinalIgnoreCase) == true);

            var modified = false;

            foreach (var packageRef in packageRefs)
            {
                // Handle Version attribute
                var versionAttr = packageRef.Attribute("Version");
                if (versionAttr != null && versionAttr.Value != targetVersion)
                {
                    versionAttr.Value = targetVersion;
                    modified = true;
                }

                // Handle nested Version element
                var versionElement = packageRef.Element("Version");
                if (versionElement != null && versionElement.Value != targetVersion)
                {
                    versionElement.Value = targetVersion;
                    modified = true;
                }
            }

            if (!modified)
            {
                return null;
            }

            var newContent = doc.ToString();

            if (!dryRun)
            {
                File.WriteAllText(projectPath, newContent);
            }

            return new FileChange(
                projectPath,
                "Modified",
                $"Version: various",
                $"Version: {targetVersion}"
            );
        }
        catch (Exception)
        {
            // File may be locked, inaccessible, or contain invalid XML
            return null;
        }
    }
}
