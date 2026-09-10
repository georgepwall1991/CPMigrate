using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Removes the inline <c>Version</c> from a <c>&lt;PackageReference&gt;</c> so the central pin in
/// <c>Directory.Packages.props</c> applies — the fix the <c>InlineVersionUnderCpm</c> finding's own
/// hint names. A <c>VersionOverride</c> is NuGet's supported per-project escape hatch and stays:
/// the analyzer reports it at a lower severity because it is deliberate, and deleting it would
/// silently change which version the project restores.
/// </summary>
public class InlineVersionFixer : IFixer
{
    public string Name => "Inline Version Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.InlineVersionUnderCpm;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        var changes = new List<FileChange>();
        List<string> failures = [];

        // AffectedProjects carries project ids (paths relative to the scan root), not file names —
        // same resolution RedundantReferenceFixer keeps.
        foreach (var projectId in issue.AffectedProjects)
        {
            var projectPath = packageInfo.ResolveProjectPath(projectId);

            if (projectPath == null || !File.Exists(projectPath))
            {
                continue;
            }

            try
            {
                var result = RemoveInlineVersion(projectPath, issue.PackageName, request.DryRun);
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
                    $"No inline Version found for {issue.PackageName} — a VersionOverride is deliberate and stays"
                );
        }

        var description =
            $"Removed inline Version for {issue.PackageName} in {changes.Count} project(s)";

        return failures.Count > 0
            ? FixResult.PartiallyApplied(
                $"{description}, but could not change {failures.Count} other file(s): {string.Join("; ", failures)}",
                changes
            )
            : FixResult.Succeeded(description, changes);
    }

    /// <summary>
    /// Removes the <c>Version</c> attribute or <c>&lt;Version&gt;</c> child from the package's
    /// <c>&lt;PackageReference&gt;</c>. Returns null when the reference has none — a
    /// <c>VersionOverride</c> finding names the same package but carries no inline version to
    /// remove.
    /// </summary>
    private static FileChange? RemoveInlineVersion(string projectPath, string packageName, bool dryRun)
    {
        try
        {
            var originalContent = File.ReadAllText(projectPath);
            var doc = XDocument.Parse(originalContent);

            var removed = 0;
            foreach (var reference in doc.Descendants("PackageReference"))
            {
                var name = reference.Attribute("Include")?.Value
                    ?? reference.Attribute("Update")?.Value;
                if (!string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The attribute form is the common one; the child-element form is legal MSBuild and
                // carries the same override.
                var attribute = reference.Attribute("Version");
                var child = reference.Elements("Version").FirstOrDefault();
                if (attribute is null && child is null)
                {
                    continue;
                }

                attribute?.Remove();
                child?.Remove();
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
                $"Version on {packageName}",
                "central pin applies"
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
