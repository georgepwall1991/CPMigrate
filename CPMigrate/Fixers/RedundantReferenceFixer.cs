using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Fixes redundant package references by removing duplicates within the same project.
/// </summary>
public class RedundantReferenceFixer : IFixer
{
    public string Name => "Redundant Reference Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.RedundantReference;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        var changes = new List<FileChange>();
        List<string> failures = [];

        // AffectedProjects carries project ids (paths relative to the scan root), not file names — this
        // used to match against ProjectName, which never matched, so the fixer silently found nothing to
        // do on every finding it was handed.
        foreach (var projectId in issue.AffectedProjects)
        {
            var projectPath = packageInfo.ResolveProjectPath(projectId);

            if (projectPath == null || !File.Exists(projectPath))
            {
                continue;
            }

            try
            {
                var result = RemoveDuplicateReferences(projectPath, issue.PackageName, request.DryRun);
                if (result != null)
                {
                    changes.Add(result);
                }
            }
            catch (FixWriteException ex)
            {
                // One file that cannot be read or written must not stop the others — but it must not pass
                // for "nothing to change" either, which is what swallowing it did.
                failures.Add(ex.Message);
            }
        }

        if (changes.Count == 0)
        {
            return failures.Count > 0
                ? FixResult.Failed(string.Join("; ", failures))
                : FixResult.NoFixNeeded($"No redundant references found for {issue.PackageName}");
        }

        var description =
            $"Removed redundant references for {issue.PackageName} in {changes.Count} project(s)";

        return FixResult.Succeeded(
            failures.Count > 0
                ? $"{description}. Could not change {failures.Count} other file(s): {string.Join("; ", failures)}"
                : description,
            changes
        );
    }

    /// <summary>
    /// Whether a declaration sits under any <c>Condition</c>. The whole ancestor chain, because a
    /// declaration inside <c>&lt;Choose&gt;&lt;When Condition=…&gt;</c> has none on itself or its group.
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

    private static FileChange? RemoveDuplicateReferences(string projectPath, string packageName, bool dryRun)
    {
        try
        {
            var originalContent = File.ReadAllText(projectPath);
            var doc = XDocument.Parse(originalContent);

            var packageRefs = doc.Descendants("PackageReference")
                .Where(e => e.Attribute("Include")?.Value
                    .Equals(packageName, StringComparison.OrdinalIgnoreCase) == true)
                // Conditional declarations are not candidates for removal, whatever else the file
                // contains. A project can hold two unconditional duplicates *and* a framework-specific
                // declaration; removing everything after the first would then delete the very thing the
                // analyzer's condition filter exists to protect, and the fix would read as a tidy-up.
                .Where(e => !IsConditional(e))
                .ToList();

            if (packageRefs.Count <= 1)
            {
                return null;
            }

            // Keep the first reference, remove the unconditional duplicates behind it.
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
        catch (Exception ex)
        {
            // Swallowed, this returned null — which the caller could only read as "nothing to change", so
            // a project file that was read-only, locked, or malformed produced "No changes were needed"
            // over a finding that had just been reported as fixable. The exit code still gated on the
            // rescan, but the message stated the opposite of what happened and hid the real problem: a
            // permission or parse error nobody was told about.
            throw new FixWriteException(projectPath, ex);
        }
    }
}
