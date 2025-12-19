using System.Xml.Linq;
using CPMigrate.Models;

namespace CPMigrate.Fixers;

/// <summary>
/// Fixes duplicate package name casing by standardizing to a consistent case.
/// </summary>
public class DuplicatePackageFixer : IFixer
{
    public string Name => "Duplicate Package Casing Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        // This fixer handles issues about casing variations
        return issue.Description.Contains("casing variations") ||
               issue.Description.Contains("different casings");
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        // Find all references for this package (case-insensitive)
        var references = packageInfo.References
            .Where(r => r.PackageName.Equals(issue.PackageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (references.Count == 0)
        {
            return FixResult.NoFixNeeded($"No references found for {issue.PackageName}");
        }

        // Find the most common casing (or the first one found)
        var casingGroups = references
            .GroupBy(r => r.PackageName)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (casingGroups.Count <= 1)
        {
            return FixResult.NoFixNeeded($"No casing variations for {issue.PackageName}");
        }

        // Use the most common casing as the standard
        var standardCasing = casingGroups.First().Key;

        var changes = new List<FileChange>();

        // Find projects with non-standard casing
        var nonStandardRefs = references
            .Where(r => r.PackageName != standardCasing)
            .GroupBy(r => r.ProjectPath);

        foreach (var group in nonStandardRefs)
        {
            var projectPath = group.Key;
            var result = StandardizePackageCasing(projectPath, issue.PackageName, standardCasing, dryRun);
            if (result != null)
            {
                changes.Add(result);
            }
        }

        if (changes.Count == 0)
        {
            return FixResult.NoFixNeeded($"All references already use consistent casing");
        }

        return FixResult.Succeeded(
            $"Standardized {issue.PackageName} casing to '{standardCasing}' in {changes.Count} project(s)",
            changes
        );
    }

    private static FileChange? StandardizePackageCasing(string projectPath, string packageNameInsensitive, string standardCasing, bool dryRun)
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
                    .Equals(packageNameInsensitive, StringComparison.OrdinalIgnoreCase) == true);

            var modified = false;
            var oldCasings = new List<string>();

            foreach (var packageRef in packageRefs)
            {
                var includeAttr = packageRef.Attribute("Include");
                if (includeAttr != null && includeAttr.Value != standardCasing)
                {
                    oldCasings.Add(includeAttr.Value);
                    includeAttr.Value = standardCasing;
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
                string.Join(", ", oldCasings.Distinct()),
                standardCasing
            );
        }
        catch (Exception)
        {
            // File may be locked, inaccessible, or contain invalid XML
            return null;
        }
    }
}
