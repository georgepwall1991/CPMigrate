using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Removes a central <c>&lt;PackageVersion&gt;</c> entry no project references — the fix the
/// <c>OrphanedPackageVersion</c> finding's own hint already names. The props file the finding
/// points at comes from its metadata: the conventional root file when absent, the relative path
/// it carries when a nested or redirected file produced the finding.
/// </summary>
public class OrphanedPackageVersionFixer : IFixer
{
    public string Name => "Orphaned Package Version Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.OrphanedPackageVersion;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        // The finding names the file through metadata when it is not the conventional root file;
        // absent means the root file, which is the only one that could produce a finding before
        // nested files were read.
        var propsFile = issue.Metadata?.GetValueOrDefault("propsFile") ?? "Directory.Packages.props";
        var propsPath = Path.IsPathRooted(propsFile)
            ? propsFile
            : Path.GetFullPath(Path.Combine(packageInfo.BasePath ?? ".", propsFile));

        if (!File.Exists(propsPath))
        {
            return FixResult.Failed($"Props file not found: {propsPath}");
        }

        try
        {
            var originalContent = File.ReadAllText(propsPath);
            var doc = XDocument.Parse(originalContent);

            // PackageVersion entries declare the pin two ways — Include for a new pin, Update for
            // amending an existing one — and either can be the orphaned entry. Conditional entries
            // are not candidates: the analyzer keeps them out of the set it reports orphans from,
            // so removing one here would delete a pin the finding never named.
            var entries = doc
                .Descendants("PackageVersion")
                .Where(e =>
                    string.Equals(
                        e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value,
                        issue.PackageName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Where(e => !IsConditional(e))
                .ToList();

            if (entries.Count == 0)
            {
                return FixResult.NoFixNeeded(
                    $"No PackageVersion entry for {issue.PackageName} in {propsPath}"
                );
            }

            foreach (var entry in entries)
            {
                entry.Remove();
            }

            var newContent = doc.ToString();
            if (!request.DryRun)
            {
                File.WriteAllText(propsPath, newContent);
            }

            return FixResult.Succeeded(
                $"Removed orphaned PackageVersion for {issue.PackageName} from {propsPath}",
                [
                    new FileChange(
                        propsPath,
                        "Modified",
                        $"{entries.Count} PackageVersion entr{(entries.Count == 1 ? "y" : "ies")}",
                        "removed"
                    ),
                ]
            );
        }
        catch (Exception ex)
        {
            // Same contract the other fixers keep: a read-only, locked, or malformed props file is
            // a failure with a cause, not "nothing to change".
            throw new FixWriteException(propsPath, ex);
        }
    }

    /// <summary>
    /// Whether a declaration sits under any <c>Condition</c>. The whole ancestor chain, because a
    /// declaration inside <c>&lt;Choose&gt;&lt;When Condition=…&gt;</c> has none on itself or its
    /// group — same semantics <see cref="RedundantReferenceFixer"/> keeps.
    /// </summary>
    private static bool IsConditional(XElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Attribute("Condition")?.Value))
            {
                return true;
            }

            // <Otherwise> carries no Condition attribute but is conditional by definition — it
            // applies exactly when none of its sibling <When> branches did.
            if (current.Name.LocalName == "Otherwise")
            {
                return true;
            }
        }

        return false;
    }
}
