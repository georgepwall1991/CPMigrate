using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;
using NuGet.Versioning;

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
        return issue.IssueCode == AnalysisIssueCode.VersionInconsistency;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        // Find all references for this package, excluding conditional pins by the same rule the analyzer
        // uses. Without that, the version chosen here is drawn from references the *finding* never
        // compared: a framework-conditional 99.0 in one project would drag unconditional 1.0 and 2.0 in
        // others up to 99.0, on the strength of a report that only mentioned 1.0 and 2.0. Skipping the
        // conditional declaration when writing is not enough if it still decides what gets written.
        var references = packageInfo.References
            .Where(r => r.PackageName.Equals(issue.PackageName, StringComparison.OrdinalIgnoreCase))
            .Where(r => !packageInfo.IsConditionallyDeclared(r.ProjectPath, r.PackageName, r.Version))
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

        var targetVersion = ResolveVersion(versions, request.ConflictStrategy);
        if (targetVersion == null)
        {
            return FixResult.Failed($"Cannot resolve version for {issue.PackageName} with Fail strategy");
        }

        var changes = new List<FileChange>();

        // Group by project file to process each file once
        var projectGroups = references
            .Where(r => r.Version != targetVersion)
            .GroupBy(r => r.ProjectPath);

        var projectResults = projectGroups
            .Select(group => UpdateProjectVersions(group.Key, issue.PackageName, targetVersion, request.DryRun))
            .Where(result => result != null)
            .Cast<FileChange>();

        changes.AddRange(projectResults);

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
            _ => versions[0]
        };
    }

    private static NuGetVersion ParseVersion(string versionString)
    {
        if (NuGetVersion.TryParse(versionString, out var version))
        {
            return version;
        }
        // Return lowest possible version so unparseable strings sort to the bottom
        return new NuGetVersion(0, 0, 0);
    }

    /// <summary>
    /// Whether a declaration, or an ancestor holding it, carries a <c>Condition</c> — which makes it
    /// framework- or configuration-specific and not ours to rewrite.
    /// </summary>
    private static bool IsConditional(XElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Attribute("Condition")?.Value))
            {
                return true;
            }

            // <Otherwise> carries no Condition attribute but is conditional by definition — it applies
            // exactly when none of its sibling <When> branches did. Reading it as unconditional let a
            // duplicate elsewhere in the file authorise deleting or rewriting the fallback branch.
            if (current.Name.LocalName == "Otherwise")
            {
                return true;
            }
        }

        return false;
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
                    .Equals(packageName, StringComparison.OrdinalIgnoreCase) == true)
                // A conditional declaration is deliberate: a multi-targeted project pinning a different
                // version per framework is the ordinary way to express "the newer one does not support
                // net8.0". Unifying them to the highest version silently breaks the target that needed the
                // older one — and the fix reads as a tidy-up, so nobody looks here when the build goes red.
                // Overlap cannot be evaluated outside a build, so a conditional pin is left alone.
                .Where(e => !IsConditional(e));

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
