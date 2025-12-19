using System.Xml.Linq;
using CPMigrate.Models;

namespace CPMigrate.Fixers;

/// <summary>
/// Fixes redundant package references by removing duplicates within the same project.
/// </summary>
public class RedundantReferenceFixer : IFixer
{
    public string Name => "Redundant Reference Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        // This fixer handles issues about redundant references in same project
        return issue.Description.Contains("redundant") ||
               issue.Description.Contains("appears") && issue.Description.Contains("times");
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        var changes = new List<FileChange>();

        // Process each affected project
        foreach (var projectName in issue.AffectedProjects)
        {
            // Find the full path for this project
            var projectPath = packageInfo.References
                .FirstOrDefault(r => r.ProjectName == projectName)?.ProjectPath;

            if (projectPath == null || !File.Exists(projectPath))
            {
                continue;
            }

            var result = RemoveDuplicateReferences(projectPath, issue.PackageName, dryRun);
            if (result != null)
            {
                changes.Add(result);
            }
        }

        if (changes.Count == 0)
        {
            return FixResult.NoFixNeeded($"No redundant references found for {issue.PackageName}");
        }

        return FixResult.Succeeded(
            $"Removed redundant references for {issue.PackageName} in {changes.Count} project(s)",
            changes
        );
    }

    private static FileChange? RemoveDuplicateReferences(string projectPath, string packageName, bool dryRun)
    {
        try
        {
            var originalContent = File.ReadAllText(projectPath);
            var doc = XDocument.Parse(originalContent);

            var packageRefs = doc.Descendants("PackageReference")
                .Where(e => e.Attribute("Include")?.Value
                    .Equals(packageName, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            if (packageRefs.Count <= 1)
            {
                return null;
            }

            // Keep the first reference, remove duplicates
            var toRemove = packageRefs.Skip(1).ToList();
            var removedCount = 0;

            foreach (var duplicate in toRemove)
            {
                duplicate.Remove();
                removedCount++;
            }

            if (removedCount == 0)
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
                $"{packageRefs.Count} references",
                "1 reference"
            );
        }
        catch (Exception)
        {
            // File may be locked, inaccessible, or contain invalid XML
            return null;
        }
    }
}
