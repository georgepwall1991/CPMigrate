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
                var result = RemoveInlineVersion(projectPath, issue.PackageName, request);
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
    private static FileChange? RemoveInlineVersion(string projectPath, string packageName, FixRequest request)
    {
        try
        {
            var originalContent = request.ReadFile(projectPath);
            var doc = XDocument.Parse(originalContent);

            var changed = 0;
            var renamed = 0;
            foreach (var reference in doc.Descendants("PackageReference"))
            {
                var name = reference.Attribute("Include")?.Value
                    ?? reference.Attribute("Update")?.Value;
                if (!string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The attribute form is the common one; the child-element form is legal MSBuild and
                // carries the same override. A reference can declare the element twice and item
                // metadata is last-wins, so every <Version> child must be handled — removing only
                // the first would leave a second one still overriding the central pin.
                var attribute = reference.Attribute("Version");
                var children = reference.Elements("Version").ToList();
                if (attribute is null && children.Count == 0)
                {
                    continue;
                }

                // An existing VersionOverride already decides this reference's version, so a stray
                // Version next to it is dead weight — removing it changes nothing resolved.
                var hasOverride =
                    reference.Attribute("VersionOverride") is not null
                    || reference.Elements("VersionOverride").Any();

                // An MSBuild expression cannot be dropped onto the central pin without rebinding
                // the project — "$(X)" evaluates in project scope, against properties the props
                // file may not see. Renaming to VersionOverride evaluates in the same scope, is
                // legal under CPM, and still clears the finding. A literal or range has no such
                // indirection to preserve: the finding's own fix is removal.
                if (!hasOverride && IsExpression(attribute?.Value))
                {
                    reference.SetAttributeValue("VersionOverride", attribute!.Value);
                    attribute.Remove();
                    renamed++;
                }
                else
                {
                    attribute?.Remove();
                }

                foreach (var child in children)
                {
                    if (!hasOverride && IsExpression(child.Value))
                    {
                        child.Name = "VersionOverride";
                        renamed++;
                    }
                    else
                    {
                        child.Remove();
                    }
                }

                changed++;
            }

            if (changed == 0)
            {
                return null;
            }

            var newContent = doc.ToString();
            request.WriteFile(projectPath, newContent);

            return new FileChange(
                projectPath,
                "Modified",
                $"Version on {packageName}",
                renamed > 0 ? "expression preserved as VersionOverride" : "central pin applies"
            );
        }
        catch (Exception ex) when (ex is not FixWriteException)
        {
            // Same contract the other fixers keep: a read-only, locked, or malformed project file is
            // a failure with a cause, not "nothing to change".
            throw new FixWriteException(projectPath, ex);
        }
    }

    /// <summary>
    /// Whether a metadata value is an MSBuild expression — <c>$(prop)</c>, <c>@(item)</c>, or
    /// <c>%(metadata)</c> — rather than a literal version. Mirrors the migration writer's rule.
    /// </summary>
    private static bool IsExpression(string? value)
    {
        return value is not null
            && (
                value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("@(", StringComparison.Ordinal)
                || value.Contains("%(", StringComparison.Ordinal)
            );
    }
}
