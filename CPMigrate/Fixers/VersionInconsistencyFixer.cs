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
            // Versionless Update records are retained for declaration-based rules, but this fixer only
            // acts on concrete resolved versions. Treating an empty value as a conflict would target an
            // Update with no version metadata and report a successful no-op over the real finding.
            .Where(r => !string.IsNullOrWhiteSpace(r.Version))
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
        List<string> failures = [];

        // Group by project file to process each file once
        var projectGroups = references
            .Where(r => r.Version != targetVersion)
            .GroupBy(r => r.ProjectPath);

        foreach (var group in projectGroups)
        {
            try
            {
                var result = UpdateProjectVersions(
                    group.Key,
                    issue.PackageName,
                    targetVersion,
                    request.DryRun);

                if (result != null)
                {
                    changes.Add(result);
                }
            }
            catch (FixWriteException ex)
            {
                // One file that cannot be read or written must not stop the others — but it must not pass
                // for "already at the target version" either, which is what swallowing it did.
                failures.Add(ex.Message);
            }
        }

        if (changes.Count == 0)
        {
            return failures.Count > 0
                ? FixResult.Failed(string.Join("; ", failures))
                : FixResult.NoFixNeeded($"All references already at {targetVersion}");
        }

        var description =
            $"Standardized {issue.PackageName} to version {targetVersion} in {changes.Count} project(s)";

        // A partial outcome is not a success: the issue survives in the files that could not be changed.
        return failures.Count > 0
            ? FixResult.PartiallyApplied(
                $"{description}, but could not change {failures.Count} other file(s): {string.Join("; ", failures)}",
                changes
            )
            : FixResult.Succeeded(description, changes);
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
                // Keep the attribute selection aligned with ProjectFileScanner: an empty Include means
                // this is an Update item, so matching Include alone would report a successful no-op for a
                // declared reference the analyzer just proved has a conflicting version.
                .Where(e =>
                    string.Equals(
                        string.IsNullOrWhiteSpace(e.Attribute("Include")?.Value)
                            ? e.Attribute("Update")?.Value
                            : e.Attribute("Include")?.Value,
                        packageName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                // A conditional declaration is deliberate: a multi-targeted project pinning a different
                // version per framework is the ordinary way to express "the newer one does not support
                // net8.0". Unifying them to the highest version silently breaks the target that needed the
                // older one — and the fix reads as a tidy-up, so nobody looks here when the build goes red.
                // Overlap cannot be evaluated outside a build, so a conditional pin is left alone.
                .Where(e => !IsConditional(e));
            var packageRefList = packageRefs.ToList();

            var modified = false;
            var containsUnresolvedVersion = false;

            for (var index = 0; index < packageRefList.Count; index++)
            {
                var packageRef = packageRefList[index];
                var ignoreSupersededPropertyVersion = IsSupersededPropertyVersion(
                    packageRefList,
                    index,
                    packageName
                );
                var metadataResults = new[]
                {
                    UpdateVersionMetadata(
                        packageRef.Attribute("Version"),
                        targetVersion,
                        ignoreSupersededPropertyVersion
                    ),
                    UpdateVersionMetadata(packageRef.Attribute("VersionOverride"), targetVersion),
                    UpdateVersionMetadata(
                        packageRef.Element("Version"),
                        targetVersion,
                        ignoreSupersededPropertyVersion
                    ),
                    UpdateVersionMetadata(packageRef.Element("VersionOverride"), targetVersion),
                };

                modified |= metadataResults.Any(result => result.Modified);
                containsUnresolvedVersion |= metadataResults.Any(result => result.Unresolved);
            }

            if (containsUnresolvedVersion)
            {
                throw new InvalidOperationException(
                    $"Cannot standardize {packageName}: the project contains an MSBuild property version"
                );
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

    private static bool IsSupersededPropertyVersion(
        IReadOnlyList<XElement> packageReferences,
        int currentIndex,
        string packageName
    )
    {
        var currentVersionValues = GetVersionMetadata(packageReferences[currentIndex]);
        if (!currentVersionValues.Any(IsPropertyVersion))
        {
            return false;
        }

        var latestUpdateVersion = packageReferences
            .Skip(currentIndex + 1)
            .Where(element =>
                string.IsNullOrWhiteSpace(element.Attribute("Include")?.Value)
                && string.Equals(
                    element.Attribute("Update")?.Value,
                    packageName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .SelectMany(GetVersionMetadata)
            .LastOrDefault();

        return latestUpdateVersion is not null && !IsPropertyVersion(latestUpdateVersion);
    }

    private static IEnumerable<string> GetVersionMetadata(XElement packageReference)
    {
        var versionAttribute = packageReference.Attribute("Version");
        if (versionAttribute is not null)
        {
            yield return versionAttribute.Value;
        }

        var versionElement = packageReference.Element("Version");
        if (versionElement is not null)
        {
            yield return versionElement.Value;
        }
    }

    private static bool IsPropertyVersion(string version) => version.Contains("$(", StringComparison.Ordinal);

    private static (bool Modified, bool Unresolved) UpdateVersionMetadata(
        XAttribute? metadata,
        string targetVersion,
        bool ignoreSupersededPropertyVersion = false
    )
    {
        if (metadata is null || metadata.Value == targetVersion)
        {
            return (false, false);
        }

        if (IsPropertyVersion(metadata.Value))
        {
            return ignoreSupersededPropertyVersion ? (false, false) : (false, true);
        }

        metadata.Value = targetVersion;
        return (true, false);
    }

    private static (bool Modified, bool Unresolved) UpdateVersionMetadata(
        XElement? metadata,
        string targetVersion,
        bool ignoreSupersededPropertyVersion = false
    )
    {
        if (metadata is null || metadata.Value == targetVersion)
        {
            return (false, false);
        }

        if (IsPropertyVersion(metadata.Value))
        {
            return ignoreSupersededPropertyVersion ? (false, false) : (false, true);
        }

        metadata.Value = targetVersion;
        return (true, false);
    }

}
