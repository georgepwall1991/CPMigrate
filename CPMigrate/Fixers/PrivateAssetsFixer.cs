using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Sets <c>PrivateAssets="all"</c> on references to development-only packages — the fix the
/// <c>DevelopmentDependencyLeak</c> finding names. Both declaration forms get the same treatment:
/// a missing metadata gets an attribute, and an existing partial value is replaced — partial
/// scoping still lets the package flow, so "some assets private" is not the fix.
/// </summary>
public class PrivateAssetsFixer : IFixer
{
    public string Name => "Private Assets Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.DevelopmentDependencyLeak;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        var changes = new List<FileChange>();
        List<string> failures = [];

        // A GlobalPackageReference finding names no project — the leak lives in the props file the
        // metadata records, and that file is where the fix goes.
        if (issue.Metadata is not null
            && issue.Metadata.TryGetValue("propsFile", out var propsFile)
            && !string.IsNullOrWhiteSpace(propsFile)
            && issue.AffectedProjects.Count == 0)
        {
            var resolvedProps = packageInfo.BasePath is null
                ? null
                : Path.Combine(packageInfo.BasePath, propsFile);
            if (resolvedProps is not null && File.Exists(resolvedProps))
            {
                try
                {
                    // An import file can inject the package either way — GlobalPackageReference or
                    // a plain PackageReference — and both kinds scope the same.
                    var result = ScopePackagePrivately(
                        resolvedProps,
                        issue.PackageName,
                        ["GlobalPackageReference", "PackageReference"],
                        request
                    );
                    if (result is not null)
                    {
                        changes.Add(result);
                    }
                }
                catch (FixWriteException ex)
                {
                    failures.Add(ex.Message);
                }
            }

            return Summarize(issue, changes, failures, "the import file");
        }

        // AffectedProjects carries project ids (paths relative to the scan root), not file names —
        // same resolution InlineVersionFixer keeps.
        foreach (var projectId in issue.AffectedProjects)
        {
            var projectPath = packageInfo.ResolveProjectPath(projectId);
            if (projectPath is null || !File.Exists(projectPath))
            {
                continue;
            }

            try
            {
                var result = ScopePackagePrivately(
                    projectPath,
                    issue.PackageName,
                    ["PackageReference"],
                    request
                );
                if (result is not null)
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

        return Summarize(issue, changes, failures, $"{changes.Count} project(s)");
    }

    private static FixResult Summarize(
        AnalysisIssue issue,
        List<FileChange> changes,
        List<string> failures,
        string scope
    )
    {
        if (changes.Count == 0)
        {
            return failures.Count > 0
                ? FixResult.Failed(string.Join("; ", failures))
                : FixResult.NoFixNeeded(
                    $"No unscoped reference to {issue.PackageName} found to update"
                );
        }

        var description =
            $"Set PrivateAssets=\"all\" for {issue.PackageName} on {scope}";

        return failures.Count > 0
            ? FixResult.PartiallyApplied(
                $"{description}, but could not change {failures.Count} other file(s): {string.Join("; ", failures)}",
                changes
            )
            : FixResult.Succeeded(description, changes);
    }

    /// <summary>
    /// Sets <c>PrivateAssets="all"</c> on every matching item of the given item types naming the
    /// package that does not already cover all assets. Items already scoped — including a
    /// <c>PackageReference Update</c> that declares coverage — are left alone.
    /// </summary>
    private static FileChange? ScopePackagePrivately(
        string filePath,
        string packageName,
        IReadOnlyList<string> itemNames,
        FixRequest request
    )
    {
        try
        {
            var originalContent = request.ReadFile(filePath);
            var doc = XDocument.Parse(originalContent);

            var touched = 0;
            foreach (var reference in itemNames.SelectMany(itemName => doc.Descendants(itemName)))
            {
                var name =
                    reference.Attribute("Include")?.Value
                    ?? reference.Attribute("Update")?.Value;
                if (!string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A conditioned PrivateAssets still leaks on every configuration the condition
                // excludes, so it is replaced rather than honoured — same judgment the analyzer
                // makes when it reads coverage. Attributes cannot carry a Condition, so the
                // attribute form is unconditional by construction.
                var attribute = reference.Attribute("PrivateAssets");
                var child = reference
                    .Elements()
                    .FirstOrDefault(e =>
                        e.Name.LocalName.Equals("PrivateAssets", StringComparison.OrdinalIgnoreCase)
                    );

                if (
                    CoversAll(attribute?.Value)
                    || (child?.Attribute("Condition") is null && CoversAll(child?.Value))
                )
                {
                    continue;
                }

                if (attribute is not null)
                {
                    attribute.Value = "all";
                }

                if (child is not null)
                {
                    child.Attribute("Condition")?.Remove();
                    child.Value = "all";
                }

                if (attribute is null && child is null)
                {
                    reference.SetAttributeValue("PrivateAssets", "all");
                }

                touched++;
            }

            if (touched == 0)
            {
                return null;
            }

            var newContent = doc.ToString();
            request.WriteFile(filePath, newContent);

            return new FileChange(
                filePath,
                "Modified",
                $"{packageName} reference",
                "PrivateAssets=\"all\""
            );
        }
        catch (Exception ex) when (ex is not FixWriteException)
        {
            // Same contract the other fixers keep: a read-only, locked, or malformed file is a
            // failure with a cause, not "nothing to change".
            throw new FixWriteException(filePath, ex);
        }
    }

    private static bool CoversAll(string? privateAssets)
    {
        if (string.IsNullOrWhiteSpace(privateAssets))
        {
            return false;
        }

        return privateAssets
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token =>
                token.Equals("all", StringComparison.OrdinalIgnoreCase)
                || token.Equals("*", StringComparison.Ordinal)
            );
    }
}
