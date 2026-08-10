using CPMigrate.Models;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CPMigrate.Services;

public sealed class ProjectFileScanner : IProjectFileScanner
{
    private readonly IConsoleService _consoleService;
    private readonly ILogger<ProjectFileScanner> _logger;

    public ProjectFileScanner(
        IConsoleService consoleService,
        ILogger<ProjectFileScanner>? logger = null
    )
    {
        _consoleService = consoleService;
        _logger = logger ?? NullLogger<ProjectFileScanner>.Instance;
    }

    public string GetTargetFramework(string projectFilePath)
    {
        try
        {
            using var projectCollection = new ProjectCollection();
            var projectRoot = ProjectRootElement.Open(projectFilePath, projectCollection);

            var targetFramework =
                projectRoot
                    .Properties.FirstOrDefault(p =>
                        p.Name == "TargetFramework" || p.Name == "TargetFrameworks"
                    )
                    ?.Value
                ?? "Unknown";

            return targetFramework;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or Microsoft.Build.Exceptions.InvalidProjectFileException
            )
        {
            return "Unknown";
        }
    }

    public string ProcessProject(
        string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false
    )
    {
        using var projectCollection = new ProjectCollection();
        var projectRoot = ProjectRootElement.Open(projectFilePath, projectCollection);

        try
        {
            foreach (var item in projectRoot.Items)
            {
                if (item.ItemType != "PackageReference")
                {
                    continue;
                }

                var versionMetadata = item.Metadata.FirstOrDefault(m => m.Name == "Version");
                if (versionMetadata == null || string.IsNullOrEmpty(versionMetadata.Value))
                {
                    continue;
                }

                if (packageVersions.TryGetValue(item.Include, out var versions))
                {
                    versions.Add(versionMetadata.Value);
                }
                else
                {
                    packageVersions.Add(item.Include, [versionMetadata.Value]);
                }

                if (!keepVersionAttributes)
                {
                    versionMetadata.Parent.RemoveChild(versionMetadata);
                }
            }

            return projectRoot.RawXml;
        }
        finally
        {
            projectCollection.UnloadAllProjects();
        }
    }

    /// <summary>
    /// Whether a declaration sits under any <c>Condition</c> at all.
    ///
    /// The whole ancestor chain, not the item and its group: a valid declaration inside
    /// <c>&lt;Choose&gt;&lt;When Condition=…&gt;&lt;ItemGroup&gt;</c> has no condition on either, so
    /// checking two levels reported it as unconditional — and two mutually exclusive declarations then read
    /// as duplicates of each other.
    /// </summary>
    private static string? GetConditionalScope(ProjectElement element)
    {
        List<string> conditions = [];
        for (ProjectElement? current = element; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Condition))
            {
                var condition = current.Condition.Trim();
                conditions.Add(
                    current is ProjectWhenElement
                        ? $"{condition}@{GetConditionalBranchPath(current)}"
                        : condition
                );
            }

            // <Otherwise> has no Condition of its own but applies exactly when no sibling <When> did.
            if (current is ProjectOtherwiseElement)
            {
                conditions.Add($"<Otherwise>@{GetConditionalBranchPath(current)}");
            }
        }

        return conditions.Count == 0 ? null : string.Join(" -> ", conditions);
    }

    private static string GetElementPath(ProjectElement element)
    {
        List<int> path = [];
        for (ProjectElement? current = element; current?.Parent is not null; current = current.Parent)
        {
            var parent = current.Parent;
            var index = 0;
            foreach (var child in parent.Children)
            {
                if (ReferenceEquals(child, current))
                {
                    break;
                }

                index++;
            }

            path.Add(index);
        }

        path.Reverse();
        return string.Join('.', path);
    }

    /// <inheritdoc />
    public (List<PackageReference> References, bool Success) ScanDeclaredPackages(
        string projectFilePath
    )
    {
        var projectName = Path.GetFileName(projectFilePath);
        List<PackageReference> references = [];

        try
        {
            using var projectCollection = new ProjectCollection();
            var projectRoot = ProjectRootElement.Open(projectFilePath, projectCollection);

            try
            {
                var items = projectRoot.Items.ToList();
                for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
                {
                    var item = items[itemIndex];
                    if (item.ItemType != "PackageReference")
                    {
                        continue;
                    }

                    var versionMetadata = item.Metadata.FirstOrDefault(m => m.Name == "Version");
                    var version = versionMetadata?.Value ?? string.Empty;

                    // Carried separately from Version: under central package management this is the
                    // version actually in force, and it can float exactly as a Version can — but a
                    // rule asking "does this project pin inline?" must not read it as one, because
                    // VersionOverride is NuGet's sanctioned way to step outside the central pin.
                    var versionOverrideMetadata = item.Metadata.FirstOrDefault(m => m.Name == "VersionOverride");
                    var versionOverride = versionOverrideMetadata?.Value;

                    // Kept rather than filtered, because "this package is declared twice, both times
                    // conditionally" is a different fact from "this package is declared twice" and only
                    // the caller knows which one it needs.
                    var conditionalScope = GetConditionalScope(item);
                    var isConditional = conditionalScope is not null;

                    // Update rather than Include is how a project *amends* a reference — attaching a
                    // VersionOverride to an inherited one, or restating a version. Reading Include
                    // alone left those with an empty package name, and a finding that names no
                    // package names nothing anyone can go and fix.
                    var isUpdate = string.IsNullOrWhiteSpace(item.Include);
                    var packageName = isUpdate ? item.Update : item.Include;

                    if (string.IsNullOrWhiteSpace(packageName))
                    {
                        continue;
                    }

                    // Metadata-only Updates change how an existing item is consumed, not which version
                    // it declares. Do not add one alongside an Include it amends, because declaration-based
                    // duplicate rules would call that a duplicate; retain a standalone one so casing and
                    // other declaration-based rules can still see the package name. Version rules filter
                    // its empty effective version from their comparisons.
                    var hasVersionMetadata =
                        versionMetadata is not null
                        // Presence matters here: VersionOverride="" explicitly clears inherited
                        // metadata even though its normalized value is empty.
                        || versionOverride is not null;
                    var hasPriorReference = isUpdate
                        && references.Any(existing =>
                            string.Equals(
                                existing.PackageName,
                                packageName,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    if (isUpdate && !hasVersionMetadata && hasPriorReference)
                    {
                        continue;
                    }

                    var reference = new PackageReference(
                        packageName,
                        version,
                        projectFilePath,
                        projectName,
                        IsTransitive: false,
                        IsConditional: isConditional,
                        VersionOverride: string.IsNullOrWhiteSpace(versionOverride)
                            ? null
                            : versionOverride.Trim()
                    )
                    {
                        HasVersionMetadata = versionMetadata is not null,
                        HasVersionOverrideMetadata = versionOverrideMetadata is not null,
                        HasConditionalUpdateVersionMetadata = isUpdate
                            && isConditional
                            && versionMetadata is not null,
                        ConditionalScope = conditionalScope,
                    };
                    if (isUpdate)
                    {
                        reference = reference with
                        {
                            IsMetadataOnlyUpdate = !hasVersionMetadata,
                            IsConditionalUpdate = isConditional,
                            ConditionalScope = conditionalScope,
                        };
                    }

                    // An unconditional, version-bearing Update amends every item already declared rather
                    // than adding another one, so recording it separately would have RedundantReference
                    // report a duplicate that does not exist, and would leave the superseded version in
                    // the list for FloatingVersion to read. Existing conditionality is preserved: an
                    // unconditional Update applies to a conditional item when that item exists, but it
                    // does not make a later conditional Include inert. A conditional Update stays separate
                    // from other conditional branches, while sequential Updates in the same branch fold.
                    // Metadata-only Updates were ignored above because they do not declare a version for
                    // these rules to compare.
                    var amendedIndices = FindAmendmentIndices(
                        references,
                        isUpdate,
                        isConditional,
                        packageName,
                        conditionalScope
                    );

                    if (amendedIndices.Count > 0)
                    {
                        ApplyAmendments(
                            references,
                            amendedIndices,
                            version,
                            versionMetadata is not null,
                            versionOverride?.Trim(),
                            versionOverrideMetadata is not null,
                            isConditional
                        );

                        continue;
                    }

                    // A conditional Update can target an inherited item represented by an earlier
                    // unconditional VersionOverride record. MSBuild retains that override when the Update
                    // changes only Version, so carry it onto the conditional projection instead of
                    // inventing a floating effective version for the branch. Resolve it only after checking
                    // for a same-scope amendment so a newer conditional override is never overwritten by an
                    // older unconditional one.
                    var inheritedVersion = FindInheritedVersion(
                        references,
                        isUpdate,
                        isConditional,
                        versionMetadata is not null,
                        packageName,
                        conditionalScope
                    );
                    var inheritedVersionOverride = FindInheritedVersionOverride(
                        references,
                        isUpdate,
                        isConditional,
                        versionOverride,
                        packageName,
                        conditionalScope
                    );

                    references.Add(
                        reference with
                        {
                            Version = inheritedVersion ?? reference.Version,
                            VersionOverride = inheritedVersionOverride ?? reference.VersionOverride,
                            HasVersionOverrideMetadata = inheritedVersionOverride is not null
                                || reference.HasVersionOverrideMetadata,
                        }
                    );
                }

                return (references, true);
            }
            finally
            {
                projectCollection.UnloadAllProjects();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read declarations from {Project}", projectName);
            return ([], false);
        }
    }

    public (List<PackageReference> References, bool Success) ScanProjectPackages(
        string projectFilePath
    )
    {
        var references = new List<PackageReference>();
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            using var projectCollection = new ProjectCollection();
            var projectRoot = ProjectRootElement.Open(projectFilePath, projectCollection);

            try
            {
                var items = projectRoot.Items.ToList();
                for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
                {
                    var item = items[itemIndex];
                    if (item.ItemType != "PackageReference")
                    {
                        continue;
                    }

                    var isUpdate = string.IsNullOrWhiteSpace(item.Include);
                    var packageName = isUpdate ? item.Update : item.Include;
                    if (string.IsNullOrWhiteSpace(packageName))
                    {
                        continue;
                    }

                    var versionMetadata = item.Metadata.FirstOrDefault(m => m.Name == "Version");
                    if (versionMetadata == null || string.IsNullOrEmpty(versionMetadata.Value))
                    {
                        continue;
                    }

                    var conditionalScope = GetConditionalScope(item);
                    var isConditional = conditionalScope is not null;
                    var amendedIndices = FindAmendmentIndices(
                        references,
                        isUpdate,
                        isConditional,
                        packageName,
                        conditionalScope
                    );

                    if (IsExpandableVersion(versionMetadata.Value))
                    {
                        _logger.LogDebug(
                            "Skipping MSBuild variable version '{Version}' for package {Package} in {Project}",
                            versionMetadata.Value,
                            packageName,
                            projectName
                        );
                        for (var amendedPosition = amendedIndices.Count - 1; amendedPosition >= 0; amendedPosition--)
                        {
                            references.RemoveAt(amendedIndices[amendedPosition]);
                        }
                        continue;
                    }

                    if (amendedIndices.Count > 0)
                    {
                        ApplyAmendments(
                            references,
                            amendedIndices,
                            versionMetadata.Value,
                            true,
                            null,
                            false,
                            isConditional
                        );
                        continue;
                    }

                    references.Add(
                            new PackageReference(
                                packageName,
                                versionMetadata.Value,
                                projectFilePath,
                                projectName,
                                IsConditional: isConditional
                            )
                            {
                                HasVersionMetadata = true,
                                HasConditionalUpdateVersionMetadata = isUpdate && isConditional,
                                IsConditionalUpdate = isUpdate && isConditional,
                                ConditionalScope = conditionalScope,
                            }
                        );
                }

                return (references, true);
            }
            finally
            {
                projectCollection.UnloadAllProjects();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not scan project: {ProjectName}", projectName);
            _consoleService.Warning($"Could not scan {projectName}: {ex.Message}");
            return (references, false);
        }
    }

    private static List<int> FindAmendmentIndices(
        IReadOnlyList<PackageReference> references,
        bool isUpdate,
        bool isConditional,
        string packageName,
        string? conditionalScope
    )
    {
        if (!isUpdate)
        {
            return [];
        }

        return references
            .Select((existing, index) => (existing, index))
            .Where(item =>
                string.Equals(
                    item.existing.PackageName,
                    packageName,
                    StringComparison.OrdinalIgnoreCase
                )
                && (
                    !isConditional
                    || (
                        item.existing.IsConditional
                        && (
                            string.Equals(
                                item.existing.ConditionalScope,
                                conditionalScope,
                                StringComparison.Ordinal
                            )
                            // A wider Update also amends projections created for nested scopes. The
                            // projection is a scanner representation of the same MSBuild item, not an
                            // independent declaration that may retain superseded metadata.
                            || IsWiderConditionalScope(
                                conditionalScope,
                                item.existing.ConditionalScope
                            )
                        )
                    )
                )
            )
            .Select(item => item.index)
            .ToList();
    }

    private static bool IsWiderConditionalScope(string? widerScope, string? narrowerScope)
    {
        if (widerScope is null || narrowerScope is null)
        {
            return false;
        }

        var widerConditions = widerScope
            .Split(" -> ", StringSplitOptions.None)
            .Select(SplitConditionalScopePart)
            .ToArray();
        var narrowerConditions = narrowerScope
            .Split(" -> ", StringSplitOptions.None)
            .Select(SplitConditionalScopePart)
            .ToArray();
        if (widerConditions.Length > narrowerConditions.Length)
        {
            return false;
        }

        // Conditions from independent ancestor branches may not form an ordered suffix, but a wider
        // conjunction still applies to a narrower one when every wider condition is present in it.
        // A pathless condition may stand for a Choose condition only when one whole scope is external;
        // otherwise a pathless nested item condition must not bridge sibling Choose branches.
        var allowExternalBranchMatch = widerConditions.All(part => part.BranchPath is null)
            || narrowerConditions.All(part => part.BranchPath is null);
        return widerConditions.All(condition =>
            narrowerConditions.Any(narrowerCondition =>
                ConditionalScopeConditionsMatch(
                    condition,
                    narrowerCondition,
                    allowExternalBranchMatch
                )
            )
        );
    }

    private static bool ConditionalScopeConditionsMatch(
        (string Condition, string? BranchPath) left,
        (string Condition, string? BranchPath) right,
        bool allowExternalBranchMatch
    )
    {
        return string.Equals(left.Condition, right.Condition, StringComparison.Ordinal)
            && (
                allowExternalBranchMatch
                    ? left.BranchPath is null
                        || right.BranchPath is null
                        || ConditionalBranchPathsMatch(
                            left.Condition,
                            left.BranchPath,
                            right.BranchPath
                        )
                    : left.BranchPath is not null
                        && right.BranchPath is not null
                        && ConditionalBranchPathsMatch(
                            left.Condition,
                            left.BranchPath,
                            right.BranchPath
                        )
            );
    }

    private static bool ConditionalBranchPathsMatch(
        string condition,
        string left,
        string right
    )
    {
        if (string.Equals(condition, "<Otherwise>", StringComparison.Ordinal))
        {
            return ConditionalOtherwisePathsMatch(left, right);
        }

        var leftGuardSeparator = left.IndexOf('|');
        var rightGuardSeparator = right.IndexOf('|');
        var leftGuard = leftGuardSeparator < 0 ? null : left[(leftGuardSeparator + 1)..];
        var rightGuard = rightGuardSeparator < 0 ? null : right[(rightGuardSeparator + 1)..];
        if (!string.Equals(leftGuard, rightGuard, StringComparison.Ordinal))
        {
            return false;
        }

        var leftPath = leftGuardSeparator < 0 ? left : left[..leftGuardSeparator];
        var rightPath = rightGuardSeparator < 0 ? right : right[..rightGuardSeparator];
        var leftSeparator = leftPath.LastIndexOf('.');
        var rightSeparator = rightPath.LastIndexOf('.');
        if (leftSeparator < 0 || rightSeparator < 0)
        {
            return string.Equals(leftPath, rightPath, StringComparison.Ordinal);
        }

        var leftChoosePath = leftPath[..leftSeparator];
        var rightChoosePath = rightPath[..rightSeparator];
        return !string.Equals(leftChoosePath, rightChoosePath, StringComparison.Ordinal)
            || string.Equals(
                leftPath[(leftSeparator + 1)..],
                rightPath[(rightSeparator + 1)..],
                StringComparison.Ordinal
            );
    }

    private static bool ConditionalOtherwisePathsMatch(string left, string right)
    {
        var leftPath = BranchLocation(left);
        var rightPath = BranchLocation(right);
        var leftSeparator = leftPath.LastIndexOf('.');
        var rightSeparator = rightPath.LastIndexOf('.');
        if (
            leftSeparator >= 0
            && rightSeparator >= 0
            && string.Equals(
                leftPath[..leftSeparator],
                rightPath[..rightSeparator],
                StringComparison.Ordinal
            )
        )
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        return string.Equals(BranchGuardSignature(left), BranchGuardSignature(right), StringComparison.Ordinal);
    }

    private static string BranchLocation(string branchPath)
    {
        var guardSeparator = branchPath.IndexOf('|');
        return guardSeparator < 0 ? branchPath : branchPath[..guardSeparator];
    }

    private static string? BranchGuardSignature(string branchPath)
    {
        var guardSeparator = branchPath.IndexOf('|');
        return guardSeparator < 0 ? null : branchPath[guardSeparator..];
    }

    private static (string Condition, string? BranchPath) SplitConditionalScopePart(string part)
    {
        var separator = part.LastIndexOf('@');
        if (separator < 0 || separator == part.Length - 1)
        {
            return (part, null);
        }

        var branchPath = part[(separator + 1)..];
        var pathSeparator = branchPath.IndexOf('|');
        var choosePath = pathSeparator < 0 ? branchPath : branchPath[..pathSeparator];
        return choosePath.Length > 0
            && choosePath.All(character => char.IsDigit(character) || character == '.')
            ? (part[..separator], branchPath)
            : (part, null);
    }

    private static string GetConditionalBranchPath(ProjectElement branch)
    {
        var branchPath = GetElementPath(branch);
        if (branch.Parent is null)
        {
            return branchPath;
        }

        List<string> precedingWhenConditions = [];
        foreach (var sibling in branch.Parent.Children)
        {
            if (ReferenceEquals(sibling, branch))
            {
                break;
            }

            if (sibling is ProjectWhenElement && !string.IsNullOrEmpty(sibling.Condition))
            {
                precedingWhenConditions.Add(sibling.Condition.Trim());
            }
        }

        return precedingWhenConditions.Count == 0
            ? branchPath
            : $"{branchPath}|guards={string.Join("|", precedingWhenConditions
                .Order(StringComparer.Ordinal)
                .Select(condition => $"{condition.Length}:{condition}"))}";
    }

    private static bool IsExpandableVersion(string value)
    {
        return value.Contains("$(", StringComparison.Ordinal)
            || value.Contains("@(", StringComparison.Ordinal)
            || value.Contains("%(", StringComparison.Ordinal);
    }

    private static string? FindInheritedVersion(
        IReadOnlyList<PackageReference> references,
        bool isUpdate,
        bool isConditional,
        bool hasVersionMetadata,
        string packageName,
        string? conditionalScope
    )
    {
        if (!isUpdate || !isConditional || hasVersionMetadata)
        {
            return null;
        }

        return references
            .LastOrDefault(existing =>
                IsInheritedReference(existing, packageName, conditionalScope)
                && !string.IsNullOrWhiteSpace(existing.Version)
            )
            ?.Version;
    }

    private static string? FindInheritedVersionOverride(
        IReadOnlyList<PackageReference> references,
        bool isUpdate,
        bool isConditional,
        string? versionOverride,
        string packageName,
        string? conditionalScope
    )
    {
        if (!isUpdate || !isConditional || versionOverride is not null)
        {
            return null;
        }

        return references
            .LastOrDefault(existing =>
                IsInheritedReference(existing, packageName, conditionalScope)
                && !string.IsNullOrWhiteSpace(existing.VersionOverride)
            )
            ?.VersionOverride;
    }

    private static bool IsInheritedReference(
        PackageReference existing,
        string packageName,
        string? conditionalScope
    )
    {
        return (
            !existing.IsConditional
            || IsWiderConditionalScope(existing.ConditionalScope, conditionalScope)
        )
            && string.Equals(
                existing.PackageName,
                packageName,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static List<int> FindFoldedConditionalUpdateIndices(
        IReadOnlyList<PackageReference> references,
        IReadOnlyList<int> amendedIndices,
        string? versionOverride
    )
    {
        var conditionalUpdateIndices = amendedIndices
            .Where(index =>
                references[index].IsConditionalUpdate
                && !ConditionalUpdateMetadataSurvives(references[index], versionOverride)
            )
            .ToList();
        if (conditionalUpdateIndices.Count == 0)
        {
            return [];
        }

        var hasItemDeclaration = amendedIndices.Any(index => !references[index].IsConditionalUpdate);
        var hasSurvivingConditionalUpdate = amendedIndices.Any(index =>
            ConditionalUpdateMetadataSurvives(references[index], versionOverride)
        );
        return hasItemDeclaration || hasSurvivingConditionalUpdate
            ? conditionalUpdateIndices
            : conditionalUpdateIndices.Skip(1).ToList();
    }

    private static void ApplyAmendments(
        List<PackageReference> references,
        IReadOnlyList<int> amendedIndices,
        string version,
        bool hasVersionMetadata,
        string? versionOverride,
        bool hasVersionOverrideMetadata,
        bool isConditional
    )
    {
        var foldsConditionalUpdates =
            isConditional && amendedIndices.All(index => references[index].IsConditionalUpdate);
        var hasUnconditionalReference = amendedIndices.Any(index => !references[index].IsConditional);
        var hasConditionalReference = amendedIndices.Any(index => references[index].IsConditional);
        var hasSurvivingConditionalClear = amendedIndices.Any(index =>
            ConditionalUpdateMetadataSurvives(references[index], versionOverride)
            && IsExplicitVersionOverrideClear(references[index])
        );
        var unconditionalRecordTemplate =
            !isConditional
                && !hasUnconditionalReference
                && hasConditionalReference
                && !hasSurvivingConditionalClear
                && (
                    !string.IsNullOrWhiteSpace(version)
                    || !string.IsNullOrWhiteSpace(versionOverride)
                )
                ? amendedIndices
                    .Select(index => references[index])
                    .FirstOrDefault(existing =>
                        existing.IsConditional
                        && !IsExplicitVersionOverrideClear(existing)
                        && (
                            !existing.IsConditionalUpdate
                            || (
                                ConditionalUpdateMetadataSurvives(existing, versionOverride)
                                && !string.IsNullOrWhiteSpace(existing.VersionOverride)
                            )
                        )
                    )
                : null;
        var foldedIndices = FindFoldedConditionalUpdateIndices(
            references,
            amendedIndices,
            versionOverride
        );
        foreach (var amendedIndex in amendedIndices)
        {
            var existing = references[amendedIndex];
            var conditionalMetadataSurvives = ConditionalUpdateMetadataSurvives(
                existing,
                versionOverride
            );
            references[amendedIndex] = existing with
            {
                Version = hasVersionMetadata ? version : existing.Version,
                HasVersionMetadata = hasVersionMetadata || existing.HasVersionMetadata,
                HasConditionalUpdateVersionMetadata = PropagateConditionalUpdateVersionMetadata(
                    existing,
                    isConditional,
                    hasVersionMetadata
                ),
                IsConditional = foldsConditionalUpdates
                    || (existing.IsConditional && (!existing.IsConditionalUpdate || conditionalMetadataSurvives)),
                VersionOverride = GetAmendedVersionOverride(existing, versionOverride),
                HasVersionOverrideMetadata = hasVersionOverrideMetadata
                    || existing.HasVersionOverrideMetadata,
                IsMetadataOnlyUpdate = false,
                IsConditionalUpdate = foldsConditionalUpdates
                    || (existing.IsConditionalUpdate && conditionalMetadataSurvives),
            };
        }

        for (var foldedPosition = foldedIndices.Count - 1; foldedPosition >= 0; foldedPosition--)
        {
            references.RemoveAt(foldedIndices[foldedPosition]);
        }

        // A conditional declaration can amend an inherited item without creating an unconditional Include.
        // When a later unconditional Update supplies the ordinary version, retain both facts: the
        // conditional declaration for its target and an unconditional record for the base Update. Collapsing
        // them into the conditional record hides a possible inherited base version from cross-project drift
        // analysis.
        if (unconditionalRecordTemplate is not null)
        {
            references.Add(
                unconditionalRecordTemplate with
                {
                    Version = version,
                    HasVersionMetadata = hasVersionMetadata,
                    HasConditionalUpdateVersionMetadata = false,
                    IsConditional = false,
                    VersionOverride = versionOverride,
                    HasVersionOverrideMetadata = hasVersionOverrideMetadata,
                    IsMetadataOnlyUpdate = false,
                    IsConditionalUpdate = false,
                    ConditionalScope = null,
                }
            );
        }
    }

    private static bool PropagateConditionalUpdateVersionMetadata(
        PackageReference existing,
        bool isConditional,
        bool hasVersionMetadata
    )
    {
        return existing.HasConditionalUpdateVersionMetadata
            || (isConditional && hasVersionMetadata && existing.IsConditionalUpdate);
    }

    private static string? GetAmendedVersionOverride(
        PackageReference existing,
        string? versionOverride
    )
    {
        if (versionOverride is null)
        {
            return existing.VersionOverride;
        }

        return string.IsNullOrWhiteSpace(versionOverride) ? null : versionOverride;
    }

    private static bool IsExplicitVersionOverrideClear(PackageReference reference)
    {
        return reference.HasVersionOverrideMetadata && string.IsNullOrWhiteSpace(reference.VersionOverride);
    }

    private static bool ConditionalUpdateMetadataSurvives(
        PackageReference existing,
        string? versionOverride
    )
    {
        return existing.IsConditionalUpdate
            && (
                (versionOverride is null && existing.HasVersionOverrideMetadata)
                || (
                    versionOverride is not null
                    && existing.HasConditionalUpdateVersionMetadata
                )
            );
    }
}
