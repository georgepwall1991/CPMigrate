using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Fixes duplicate package name casing by standardizing to a consistent case.
/// </summary>
public class DuplicatePackageFixer : IFixer
{
    public string Name => "Duplicate Package Casing Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.DuplicatePackageCasing;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        // Find all references for this package (case-insensitive)
        var references = packageInfo.GetDeclaredReferences()
            .Where(r => !r.IsTransitive)
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
        var standardCasing = casingGroups[0].Key;

        // Find projects with non-standard casing
        var nonStandardRefs = references
            .Where(r => r.PackageName != standardCasing)
            .GroupBy(r => r.ProjectPath);

        var changes = nonStandardRefs
            .Select(group => StandardizePackageCasing(group.Key, issue.PackageName, standardCasing, request.DryRun))
            .Where(result => result != null)
            .Cast<FileChange>()
            .ToList();

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

            // Keep the attribute selection identical to ProjectFileScanner: an empty Include means
            // the item is an Update, so choosing Include by null-coalescing would silently no-op.
            var packageRefs = doc.Descendants("PackageReference")
                .Select(e => string.IsNullOrWhiteSpace(e.Attribute("Include")?.Value)
                    ? e.Attribute("Update")
                    : e.Attribute("Include"))
                .OfType<XAttribute>()
                .Where(attribute => GetPackageNames(attribute.Value)
                    .Any(name => name.Equals(packageNameInsensitive, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var modified = false;
            var oldCasings = new List<string>();

            foreach (var packageRef in packageRefs)
            {
                var packageNames = GetPackageNames(packageRef.Value).ToList();
                var updatedNames = packageNames
                    .Select(name => name.Equals(packageNameInsensitive, StringComparison.OrdinalIgnoreCase)
                        ? standardCasing
                        : name)
                    .ToList();
                if (!packageNames.SequenceEqual(updatedNames, StringComparer.Ordinal))
                {
                    oldCasings.AddRange(
                        packageNames
                            .Zip(updatedNames)
                            .Where(pair => !string.Equals(pair.First, pair.Second, StringComparison.Ordinal))
                            .Select(pair => pair.First)
                    );
                    packageRef.Value = string.Join(';', updatedNames);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // File may be locked, inaccessible, or contain invalid XML.
            // Returns null to skip this file; callers handle the absence gracefully.
            return null;
        }
    }

    private static IEnumerable<string> GetPackageNames(string specification)
    {
        return specification.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
    }
}
