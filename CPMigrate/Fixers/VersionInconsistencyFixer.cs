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

        var unsafePackageGroup = projectGroups.FirstOrDefault(group =>
            HasMultiplePackageDeclaration(group.Key, issue.PackageName)
            || HasActiveUnconditionalVersionClear(group.Key, issue.PackageName)
            || HasConditionedPackageMetadata(group.Key, issue.PackageName)
        );
        if (unsafePackageGroup is not null)
        {
            return FixResult.Failed(
                $"Cannot standardize {issue.PackageName}: a PackageReference declaration targets multiple packages or has condition-bearing version metadata"
            );
        }

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
        if (
            element.Elements().Any(metadata =>
                (metadata.Name.LocalName == "Version"
                    || metadata.Name.LocalName == "VersionOverride")
                && !string.IsNullOrWhiteSpace(metadata.Attribute("Condition")?.Value)
            )
        )
        {
            return true;
        }

        return HasConditionalScope(element);
    }

    private static bool HasConditionalScope(XElement element)
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

    private static bool HasUnconditionalVersionMetadata(XElement packageReference)
    {
        if (
            !HasConditionalScope(packageReference)
            && (
                packageReference.Attribute("Version") is not null
                || packageReference.Attribute("VersionOverride") is not null
            )
        )
        {
            return true;
        }

        return packageReference
            .Elements()
            .Any(metadata =>
                (metadata.Name.LocalName == "Version"
                    || metadata.Name.LocalName == "VersionOverride")
                && !HasConditionalScope(metadata)
            );
    }

    private static XElement? GetUnconditionalMetadataElement(
        XElement packageReference,
        string metadataName
    )
    {
        return packageReference
            .Elements(metadataName)
            .FirstOrDefault(metadata => !HasConditionalScope(metadata));
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
            var packageRefList = allPackageRefs
                .Where(HasUnconditionalVersionMetadata)
                .ToList();

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
                        GetUnconditionalMetadataElement(packageRef, "Version"),
                        targetVersion,
                        ignoreSupersededPropertyVersion,
                        preserveOrdinaryVersion
                    ),
                    UpdateVersionMetadata(
                        GetUnconditionalMetadataElement(packageRef, "VersionOverride"),
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
        var currentMetadataValues = GetUnconditionalMetadataValues(
            packageReferences[currentIndex],
            metadataName
        );
        if (!currentMetadataValues.Any(IsExpandableMetadata))
        {
            return false;
        }

        if (metadataName == "VersionOverride")
        {
            return packageReferences
                .Skip(currentIndex + 1)
                .Where(element => IsMatchingUpdate(element, packageName))
                .Any(element => GetUnconditionalMetadataValues(element, metadataName).Any());
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
            var precedingOverride = GetUnconditionalMetadataValues(
                    element,
                    "VersionOverride"
                )
                .LastOrDefault();
            if (precedingOverride is not null)
            {
                overrideIsActive = !string.IsNullOrWhiteSpace(precedingOverride);
            }
        }

        var currentOverride = GetUnconditionalMetadataValues(
                packageReferences[currentIndex],
                "VersionOverride"
            )
            .LastOrDefault();
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
            var updatedVersion = GetUnconditionalMetadataValues(element, "Version").LastOrDefault();
            if (updatedVersion is not null)
            {
                ordinaryVersionIsCurrent = false;
            }

            var updatedOverride = GetUnconditionalMetadataValues(
                    element,
                    "VersionOverride"
                )
                .LastOrDefault();
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
        var currentIndex = -1;
        for (var index = 0; index < packageReferences.Count; index++)
        {
            if (ReferenceEquals(packageReferences[index], currentReference))
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return false;
        }

        var itemStates = new List<(
            XElement? Reference,
            int StartIndex,
            IReadOnlyList<string?> OverrideScopes,
            bool ConditionalOverrideClearIsActive
        )>
        {
            // An Update-only history may target an item imported from Directory.Build.props or an SDK.
            // Its inherited VersionOverride is not visible in this local XML, so begin conservatively as
            // potentially overridden until a local declaration proves otherwise.
            (null, 0, new string?[] { null }, false),
        };
        for (var index = 0; index < packageReferences.Count; index++)
        {
            var element = packageReferences[index];
            if (!IsMatchingDeclaration(element, packageName))
            {
                continue;
            }

            var isUpdate = IsMatchingUpdate(element, packageName);
            if (!isUpdate)
            {
                // A local Include establishes the visible item history. Do not let the speculative
                // Update-only sentinel continue protecting it after this point.
                itemStates.RemoveAll(state => state.Reference is null);
                var includeOverride = GetLastMetadataValueAndScope(element, "VersionOverride");
                itemStates.Add(
                    (
                        element,
                        index,
                        string.IsNullOrWhiteSpace(includeOverride.Value)
                            ? Array.Empty<string?>()
                            : new[] { includeOverride.Scope },
                        false
                    )
                );
                continue;
            }

            var overrideMetadata = GetLastMetadataValueAndScope(element, "VersionOverride");
            var overrideValue = overrideMetadata.Value;
            if (overrideValue is null)
            {
                continue;
            }

            if (!IsConditional(element))
            {
                var overrideScopes = string.IsNullOrWhiteSpace(overrideValue)
                    ? Array.Empty<string?>()
                    : new[] { overrideMetadata.Scope };
                for (var stateIndex = 0; stateIndex < itemStates.Count; stateIndex++)
                {
                    var state = itemStates[stateIndex];
                    itemStates[stateIndex] = (
                        state.Reference,
                        state.StartIndex,
                        overrideScopes,
                        false
                    );
                }

                continue;
            }

            for (var stateIndex = 0; stateIndex < itemStates.Count; stateIndex++)
            {
                var state = itemStates[stateIndex];
                if (!string.IsNullOrWhiteSpace(overrideValue))
                {
                    itemStates[stateIndex] = (
                        state.Reference,
                        state.StartIndex,
                        state.OverrideScopes
                            .Concat(new[] { overrideMetadata.Scope })
                            .Distinct()
                            .ToArray(),
                        state.ConditionalOverrideClearIsActive
                    );
                }
                else if (
                    state.OverrideScopes.Any(scope =>
                        ConditionalScopesMayOverlap(scope, overrideMetadata.Scope)
                    )
                )
                {
                    itemStates[stateIndex] = (
                        state.Reference,
                        state.StartIndex,
                        state.OverrideScopes,
                        true
                    );
                }
            }
        }

        return IsMatchingUpdate(currentReference, packageName)
            ? itemStates.Any(state =>
                state.StartIndex <= currentIndex && state.ConditionalOverrideClearIsActive
            )
            : itemStates.Any(state =>
                ReferenceEquals(state.Reference, currentReference)
                && state.ConditionalOverrideClearIsActive
            );
    }

    private static (string? Value, string? Scope) GetLastMetadataValueAndScope(
        XElement packageReference,
        string metadataName
    )
    {
        var value = packageReference.Attribute(metadataName)?.Value;
        var scope = value is null ? null : GetConditionalScope(packageReference);
        var metadataElement = packageReference.Element(metadataName);
        if (metadataElement is not null)
        {
            value = metadataElement.Value;
            scope = GetConditionalScope(metadataElement);
        }

        return (value, scope);
    }

    private static string? GetConditionalScope(XElement element)
    {
        List<string> scopes = [];
        for (var current = element; current is not null; current = current.Parent)
        {
            var condition = current.Attribute("Condition")?.Value;
            if (!string.IsNullOrWhiteSpace(condition))
            {
                scopes.Add(condition.Trim());
            }

            if (current.Name.LocalName == "Otherwise")
            {
                scopes.Add("<Otherwise>");
            }
        }

        return scopes.Count == 0 ? null : string.Join(" -> ", scopes);
    }

    private static bool ConditionalScopesMayOverlap(string? leftScope, string? rightScope)
    {
        if (leftScope is null || rightScope is null)
        {
            return true;
        }

        var leftConditions = leftScope.Split(" -> ", StringSplitOptions.None);
        var rightConditions = rightScope.Split(" -> ", StringSplitOptions.None);
        return !leftConditions.Any(leftCondition =>
            rightConditions.Any(rightCondition =>
                AreMutuallyExclusiveConditions(leftCondition, rightCondition)
            )
        );
    }

    private static bool AreMutuallyExclusiveConditions(string left, string right)
    {
        if (
            !TryGetSimpleEqualityCondition(left, out var leftProperty, out var leftValue)
            || !TryGetSimpleEqualityCondition(right, out var rightProperty, out var rightValue)
        )
        {
            return false;
        }

        return string.Equals(leftProperty, rightProperty, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSimpleEqualityCondition(
        string condition,
        out string property,
        out string value
    )
    {
        property = string.Empty;
        value = string.Empty;
        var equalsIndex = condition.IndexOf("==", StringComparison.Ordinal);
        if (
            equalsIndex < 0
            || condition.IndexOf("==", equalsIndex + 2, StringComparison.Ordinal) >= 0
            || condition.Contains(" And ", StringComparison.OrdinalIgnoreCase)
            || condition.Contains(" Or ", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        var left = TrimConditionOperand(condition[..equalsIndex]);
        var right = TrimConditionOperand(condition[(equalsIndex + 2)..]);
        if (IsSimplePropertyReference(left))
        {
            property = left[2..^1];
            value = right;
            return true;
        }

        if (IsSimplePropertyReference(right))
        {
            property = right[2..^1];
            value = left;
            return true;
        }

        return false;
    }

    private static string TrimConditionOperand(string operand)
    {
        var trimmed = operand.Trim();
        while (
            trimmed.Length >= 2
            && (
                (trimmed[0] == '\'' && trimmed[^1] == '\'')
                || (trimmed[0] == '"' && trimmed[^1] == '"')
            )
        )
        {
            trimmed = trimmed[1..^1].Trim();
        }

        while (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool IsSimplePropertyReference(string value)
    {
        return value.StartsWith("$(", StringComparison.Ordinal)
            && value.EndsWith(')')
            && value.IndexOf("$(", 2, StringComparison.Ordinal) < 0;
    }

    private static bool IsMatchingUpdate(XElement packageReference, string packageName)
    {
        return string.IsNullOrWhiteSpace(packageReference.Attribute("Include")?.Value)
            && ContainsPackageName(packageReference.Attribute("Update")?.Value, packageName);
    }

    private static bool IsMatchingDeclaration(XElement packageReference, string packageName)
    {
        var specification = string.IsNullOrWhiteSpace(packageReference.Attribute("Include")?.Value)
            ? packageReference.Attribute("Update")?.Value
            : packageReference.Attribute("Include")?.Value;
        return ContainsPackageName(specification, packageName);
    }

    private static bool ContainsPackageName(string? specification, string packageName)
    {
        return GetPackageNames(specification)
            .Any(name => string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasMultiplePackageDeclaration(string projectPath, string packageName)
    {
        if (!File.Exists(projectPath))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(File.ReadAllText(projectPath));
            return document
                .Descendants("PackageReference")
                .Where(reference =>
                    IsMatchingDeclaration(reference, packageName)
                    && HasVersionMetadata(reference)
                )
                .Any(reference => GetPackageNames(
                    string.IsNullOrWhiteSpace(reference.Attribute("Include")?.Value)
                        ? reference.Attribute("Update")?.Value
                        : reference.Attribute("Include")?.Value
                ).Skip(1).Any());
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVersionMetadata(XElement packageReference)
    {
        return GetMetadataValues(packageReference, "Version").Any()
            || GetMetadataValues(packageReference, "VersionOverride").Any();
    }

    private static bool HasConditionedPackageMetadata(string projectPath, string packageName)
    {
        if (!File.Exists(projectPath))
        {
            return false;
        }

        try
        {
            var matchingReferences = XDocument
                .Parse(File.ReadAllText(projectPath))
                .Descendants("PackageReference")
                .Where(reference => IsMatchingDeclaration(reference, packageName))
                .ToList();
            var hasConditionedMetadata = matchingReferences.Any(reference =>
                IsConditional(reference) && HasVersionMetadata(reference)
            );
            var hasUnconditionalVersion = matchingReferences.Any(HasUnconditionalVersionMetadata);
            return hasConditionedMetadata && !hasUnconditionalVersion;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasActiveUnconditionalVersionClear(string projectPath, string packageName)
    {
        if (!File.Exists(projectPath))
        {
            return false;
        }

        try
        {
            var matchingReferences = XDocument
                .Parse(File.ReadAllText(projectPath))
                .Descendants("PackageReference")
                .Where(reference => IsMatchingDeclaration(reference, packageName))
                .ToList();
            return HasActiveUnconditionalMetadataClear(
                    matchingReferences,
                    packageName,
                    "Version"
                )
                || HasActiveUnconditionalMetadataClear(
                    matchingReferences,
                    packageName,
                    "VersionOverride"
                );
        }
        catch
        {
            return false;
        }
    }

    private static bool HasActiveUnconditionalMetadataClear(
        IReadOnlyList<XElement> matchingReferences,
        string packageName,
        string metadataName
    )
    {
        // An Update-only history may clear metadata inherited from an import, so start conservatively
        // even though the supplying declaration is not visible in this project file.
        var hasInlineMetadata = !matchingReferences.Any(reference =>
            !IsMatchingUpdate(reference, packageName)
        );
        var hasActiveClear = false;

        foreach (var reference in matchingReferences)
        {
            foreach (var value in GetUnconditionalMetadataValues(reference, metadataName))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    hasActiveClear = hasInlineMetadata;
                }
                else
                {
                    // An expandable value is superseded by the clear without exposing a concrete
                    // version this fixer could accidentally leave behind. Literal metadata, or the
                    // inherited Update-only sentinel, must remain protected.
                    hasInlineMetadata = !IsExpandableMetadata(value);
                    hasActiveClear = false;
                }
            }
        }

        return hasActiveClear;
    }

    private static IEnumerable<string> GetPackageNames(string? specification)
    {
        return specification?.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ) ?? [];
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

    private static IEnumerable<string> GetUnconditionalMetadataValues(
        XElement packageReference,
        string metadataName
    )
    {
        if (!HasConditionalScope(packageReference))
        {
            var metadataAttribute = packageReference.Attribute(metadataName);
            if (metadataAttribute is not null)
            {
                yield return metadataAttribute.Value;
            }
        }

        foreach (
            var metadataElement in packageReference.Elements(metadataName)
                .Where(metadata => !HasConditionalScope(metadata))
        )
        {
            yield return metadataElement.Value;
        }
    }

    private static bool IsExpandableMetadata(string value) =>
        value.Contains("$(", StringComparison.Ordinal)
        || value.Contains("@(", StringComparison.Ordinal)
        || value.Contains("%(", StringComparison.Ordinal);

    private static (bool Modified, bool Unresolved) UpdateVersionMetadata(
        XAttribute? metadata,
        string targetVersion,
        bool ignoreSupersededPropertyMetadata = false,
        bool skipUpdate = false
    )
    {
        if (
            metadata is null
            || skipUpdate
            || string.IsNullOrWhiteSpace(metadata.Value)
            || metadata.Value == targetVersion
        )
        {
            return (false, false);
        }

        if (IsExpandableMetadata(metadata.Value))
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
        if (
            metadata is null
            || skipUpdate
            || string.IsNullOrWhiteSpace(metadata.Value)
            || metadata.Value == targetVersion
        )
        {
            return (false, false);
        }

        if (IsExpandableMetadata(metadata.Value))
        {
            return ignoreSupersededPropertyMetadata ? (false, false) : (false, true);
        }

        metadata.Value = targetVersion;
        return (true, false);
    }

}
