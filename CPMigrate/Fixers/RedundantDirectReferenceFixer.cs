using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Removes a direct <c>&lt;PackageReference&gt;</c> that is already provided transitively — the
/// fix the <c>RedundantDirectReference</c> finding's own hint names. Only under central package
/// management: the central pin governs the transitive version, so removing the direct reference
/// is graph-neutral. Without CPM the direct reference may be the only thing holding the package
/// at its version, and removing it would silently downgrade.
/// </summary>
public class RedundantDirectReferenceFixer : IFixer
{
    public string Name => "Redundant Direct Reference Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.RedundantDirectReference;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        // Without a props file the direct reference may be the only thing holding the package at
        // its version — removing it would silently downgrade to whatever the transitive graph
        // resolves. Under CPM the central pin governs, so the removal is graph-neutral.
        if (!File.Exists(request.PropsFilePath))
        {
            // Failed, not NoFixNeeded: the issue is still present, and a refusal that reports
            // success would hide it from the fix report.
            return FixResult.Failed(
                $"No central props file at {request.PropsFilePath} — removing a direct reference "
                    + "without CPM could downgrade the package to its transitive version"
            );
        }

        var changes = new List<FileChange>();
        List<string> failures = [];

        // AffectedProjects carries project ids (paths relative to the scan root), not file names —
        // same resolution the other fixers keep.
        foreach (var projectId in issue.AffectedProjects)
        {
            var projectPath = packageInfo.ResolveProjectPath(projectId);

            if (projectPath == null || !File.Exists(projectPath))
            {
                continue;
            }

            try
            {
                var result = RemoveDirectReference(projectPath, issue.PackageName, request.DryRun);
                if (result != null)
                {
                    changes.Add(result);
                }
            }
            catch (FixWriteException ex)
            {
                // One file that cannot be read or written must not stop the others — but it must
                // not pass for "nothing to change" either.
                failures.Add(ex.Message);
            }
        }

        if (changes.Count == 0)
        {
            return failures.Count > 0
                ? FixResult.Failed(string.Join("; ", failures))
                : FixResult.NoFixNeeded(
                    $"No direct reference found for {issue.PackageName}"
                );
        }

        var description =
            $"Removed redundant direct reference for {issue.PackageName} in {changes.Count} project(s)";

        return failures.Count > 0
            ? FixResult.PartiallyApplied(
                $"{description}, but could not change {failures.Count} other file(s): {string.Join("; ", failures)}",
                changes
            )
            : FixResult.Succeeded(description, changes);
    }

    /// <summary>
    /// Removes the package's <c>&lt;PackageReference&gt;</c>. Returns null when the project has
    /// none — the finding names a package the graph already provides, so a reference that is not
    /// there is not the finding's subject.
    /// </summary>
    private static FileChange? RemoveDirectReference(string projectPath, string packageName, bool dryRun)
    {
        try
        {
            var originalContent = File.ReadAllText(projectPath);
            var doc = XDocument.Parse(originalContent);

            var removed = 0;
            foreach (var reference in doc.Descendants("PackageReference").ToList())
            {
                var name = reference.Attribute("Include")?.Value
                    ?? reference.Attribute("Update")?.Value;
                if (!string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                reference.Remove();
                removed++;
            }

            if (removed == 0)
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
                $"PackageReference {packageName}",
                "provided transitively"
            );
        }
        catch (Exception ex)
        {
            // Same contract the other fixers keep: a read-only, locked, or malformed project file is
            // a failure with a cause, not "nothing to change".
            throw new FixWriteException(projectPath, ex);
        }
    }
}
