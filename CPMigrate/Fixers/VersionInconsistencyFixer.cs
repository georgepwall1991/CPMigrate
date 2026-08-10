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

            var allPackageRefs = doc.Descendants("PackageReference")
                // Keep the attribute selection aligned with ProjectFileScanner: an empty Include means
                // this is an Update item, so matching Include alone would report a successful no-op for a
                // declared reference the analyzer just proved has a conflicting version.
                .Where(e => IsMatchingDeclaration(e, packageName))
                .ToList();
            // A conditional declaration is deliberate: a multi-targeted project pinning a different
            // version per framework is the ordinary way to express "the newer one does not support
            // net8.0". Unifying them to the highest version silently breaks the target that needed the
            // older one — and the fix reads as a tidy-up, so nobody looks here when the build goes red.
            // Overlap cannot be evaluated outside a build, so a conditional pin is left alone.
            var packageRefList = allPackageRefs.Where(e => !IsConditional(e)).ToList();

            var modified = false;
            var containsUnresolvedVersion = false;

            for (var index = 0; index < packageRefList.Count; index++)
            {
                var packageRef = packageRefList[index];
                var ignoreSupersededPropertyVersion = IsSupersededPropertyMetadata(
                    packageRefList,
                    index,
                    packageName,
                    "Version"
                );
                var ignoreSupersededPropertyOverride = IsSupersededPropertyMetadata(
                    packageRefList,
                    index,
                    packageName,
                    "VersionOverride"
                );
                var preserveOrdinaryVersion = HasConditionalOverrideClearAfter(
                    allPackageRefs,
                    packageRef,
                    packageName
                );
                var metadataResults = new[]
                {
                    UpdateVersionMetadata(
                        packageRef.Attribute("Version"),
                        targetVersion,
                        ignoreSupersededPropertyVersion,
                        preserveOrdinaryVersion
                    ),
                    UpdateVersionMetadata(
                        packageRef.Attribute("VersionOverride"),
                        targetVersion,
                        ignoreSupersededPropertyOverride
                    ),
                    UpdateVersionMetadata(
                        packageRef.Element("Version"),
                        targetVersion,
                        ignoreSupersededPropertyVersion,
                        preserveOrdinaryVersion
                    ),
                    UpdateVersionMetadata(
                        packageRef.Element("VersionOverride"),
                        targetVersion,
                        ignoreSupersededPropertyOverride
                    ),
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

    private static bool IsSupersededPropertyMetadata(
        IReadOnlyList<XElement> packageReferences,
        int currentIndex,
        string packageName,
        string metadataName
    )
    {
        var currentMetadataValues = GetMetadataValues(packageReferences[currentIndex], metadataName);
        if (!currentMetadataValues.Any(IsPropertyMetadata))
        {
            return false;
        }

        if (metadataName == "VersionOverride")
        {
            return packageReferences
                .Skip(currentIndex + 1)
                .Where(element => IsMatchingUpdate(element, packageName))
                .Any(element => GetMetadataValues(element, metadataName).Any());
        }

        var ordinaryVersionIsCurrent = true;
        var overrideIsActive = false;

        // A preceding unconditional Update can establish the effective override for a later
        // property-valued Version update. Carry that state forward so the property metadata is
        // ignored instead of blocking a fix to the literal override that actually wins.
        foreach (
            var element in packageReferences
                .Take(currentIndex)
                .Where(element => IsMatchingDeclaration(element, packageName))
        )
        {
            var precedingOverride = GetMetadataValues(element, "VersionOverride").LastOrDefault();
            if (precedingOverride is not null)
            {
                overrideIsActive = !string.IsNullOrWhiteSpace(precedingOverride);
            }
        }

        var currentOverride = GetMetadataValues(
            packageReferences[currentIndex],
            "VersionOverride"
        ).LastOrDefault();
        if (currentOverride is not null)
        {
            overrideIsActive = !string.IsNullOrWhiteSpace(currentOverride);
        }

        foreach (
            var element in packageReferences
                .Skip(currentIndex + 1)
                .Where(element => IsMatchingUpdate(element, packageName))
        )
        {
            var updatedVersion = GetMetadataValues(element, "Version").LastOrDefault();
            if (updatedVersion is not null)
            {
                ordinaryVersionIsCurrent = false;
            }

            var updatedOverride = GetMetadataValues(element, "VersionOverride").LastOrDefault();
            if (updatedOverride is not null)
            {
                overrideIsActive = !string.IsNullOrWhiteSpace(updatedOverride);
            }
        }

        return overrideIsActive || !ordinaryVersionIsCurrent;
    }

    private static bool HasConditionalOverrideClearAfter(
        IReadOnlyList<XElement> packageReferences,
        XElement currentReference,
        string packageName
    )
    {
        var currentOverride = GetMetadataValues(currentReference, "VersionOverride").LastOrDefault();
        if (currentOverride is null || string.IsNullOrWhiteSpace(currentOverride))
        {
            return false;
        }

        var currentIndex = -1;
        for (var index = 0; index < packageReferences.Count; index++)
        {
            if (ReferenceEquals(packageReferences[index], currentReference))
            {
                currentIndex = index;
                break;
            }
        }

        return currentIndex >= 0
            && packageReferences
                .Skip(currentIndex + 1)
                .Where(element => IsConditional(element) && IsMatchingUpdate(element, packageName))
                .Any(element =>
                    GetMetadataValues(element, "VersionOverride")
                        .Any(value => string.IsNullOrWhiteSpace(value))
                );
    }

    private static bool IsMatchingUpdate(XElement packageReference, string packageName)
    {
        return string.IsNullOrWhiteSpace(packageReference.Attribute("Include")?.Value)
            && string.Equals(
                packageReference.Attribute("Update")?.Value,
                packageName,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static bool IsMatchingDeclaration(XElement packageReference, string packageName)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(packageReference.Attribute("Include")?.Value)
                ? packageReference.Attribute("Update")?.Value
                : packageReference.Attribute("Include")?.Value,
            packageName,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static IEnumerable<string> GetMetadataValues(
        XElement packageReference,
        string metadataName
    )
    {
        var metadataAttribute = packageReference.Attribute(metadataName);
        if (metadataAttribute is not null)
        {
            yield return metadataAttribute.Value;
        }

        var metadataElement = packageReference.Element(metadataName);
        if (metadataElement is not null)
        {
            yield return metadataElement.Value;
        }
    }

    private static bool IsPropertyMetadata(string value) => value.Contains("$(", StringComparison.Ordinal);

    private static (bool Modified, bool Unresolved) UpdateVersionMetadata(
        XAttribute? metadata,
        string targetVersion,
        bool ignoreSupersededPropertyMetadata = false,
        bool skipUpdate = false
    )
    {
        if (metadata is null || skipUpdate || metadata.Value == targetVersion)
        {
            return (false, false);
        }

        if (IsPropertyMetadata(metadata.Value))
        {
            return ignoreSupersededPropertyMetadata ? (false, false) : (false, true);
        }

        metadata.Value = targetVersion;
        return (true, false);
    }

    private static (bool Modified, bool Unresolved) UpdateVersionMetadata(
        XElement? metadata,
        string targetVersion,
        bool ignoreSupersededPropertyMetadata = false,
        bool skipUpdate = false
    )
    {
        if (metadata is null || skipUpdate || metadata.Value == targetVersion)
        {
            return (false, false);
        }

        if (IsPropertyMetadata(metadata.Value))
        {
            return ignoreSupersededPropertyMetadata ? (false, false) : (false, true);
        }

        metadata.Value = targetVersion;
        return (true, false);
    }

}
