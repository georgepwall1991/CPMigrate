using System.Text.RegularExpressions;
using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Fixers;

/// <summary>
/// Fixes transitive conflicts by pinning the best version in Directory.Packages.props.
/// </summary>
public class TransitiveConflictFixer : IFixer
{
    private readonly VersionResolver _versionResolver;

    public TransitiveConflictFixer(VersionResolver versionResolver)
    {
        _versionResolver = versionResolver;
    }

    public string Name => "Transitive Conflict Pinning";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.Description.Contains("Transitive dependency");
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        var propsPath = Path.Combine(options.SolutionFileDir, "Directory.Packages.props");
        if (!File.Exists(propsPath))
        {
            return FixResult.Failed("Directory.Packages.props not found. Transitive pinning requires an existing CPM setup.");
        }

        // Find best version from packageInfo
        var versions = packageInfo.References
            .Where(r => r.PackageName.Equals(issue.PackageName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Version)
            .ToHashSet();

        if (versions.Count == 0) return FixResult.Failed("Could not determine versions for package.");

        var bestVersion = _versionResolver.ResolveVersion(versions, options.ConflictStrategy);
        
        var originalContent = File.ReadAllText(propsPath);
        var updatedContent = originalContent;

        // Check if package already exists in props
        if (originalContent.Contains($"Include=\"{issue.PackageName}\"", StringComparison.OrdinalIgnoreCase) ||
            originalContent.Contains($"Update=\"{issue.PackageName}\"", StringComparison.OrdinalIgnoreCase))
        {
            // Update existing version
            var pattern = $@"(PackageVersion\s+(?:Include|Update)=""{Regex.Escape(issue.PackageName)}""\s+Version="")([^""]+)("")";
            updatedContent = Regex.Replace(originalContent, pattern, $"$1{bestVersion}$3", RegexOptions.IgnoreCase);
        }
        else
        {
            // Add new entry before </ItemGroup>
            var newEntry = $"    <PackageVersion Include=\"{issue.PackageName}\" Version=\"{bestVersion}\" />\n";
            updatedContent = originalContent.Replace("</ItemGroup>", $"{newEntry}  </ItemGroup>");
        }

        if (updatedContent == originalContent)
        {
            return FixResult.NoFixNeeded("Version already aligned or package entry not found in suitable format.");
        }

        if (!dryRun)
        {
            File.WriteAllText(propsPath, updatedContent);
        }

        return FixResult.Succeeded(
            $"Pinned {issue.PackageName} to version {bestVersion} in Directory.Packages.props",
            new List<FileChange> { new FileChange(propsPath, "Modified", "...", $"Pinned {issue.PackageName} to {bestVersion}") }
        );
    }
}
