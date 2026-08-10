using System.Text;
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

    private static string? GetConditionalMetadataScope(
        params ProjectMetadataElement?[] metadataElements
    )
    {
        var conditions = metadataElements
            .Where(metadata => !string.IsNullOrWhiteSpace(metadata?.Condition))
            .Select(metadata => metadata!.Condition.Trim())
            .GroupBy(NormalizeConditionSyntax, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return conditions.Length == 0 ? null : string.Join(" || ", conditions);
    }

    private static string? CombineConditionalScopes(
        string? itemScope,
        string? metadataScope
    )
    {
        if (string.IsNullOrWhiteSpace(metadataScope))
        {
            return itemScope;
        }

        return itemScope is null ? metadataScope.Trim() : $"{itemScope} -> {metadataScope.Trim()}";
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

    private sealed record PropertyMutation(
        string PropertyName,
        string? ConditionalScope
    );

    private sealed record PropertyMutationState(
        IReadOnlyList<PropertyMutation> Mutations,
        int ImportVersion
    );

    private static Dictionary<string, PropertyMutationState> GetPropertyMutationStates(
        ProjectRootElement projectRoot
    )
    {
        Dictionary<string, PropertyMutationState> states = new(StringComparer.Ordinal);
        List<PropertyMutation> propertyMutations = [];
        var importVersion = 0;
        VisitProjectElements(projectRoot, propertyMutations, ref importVersion, states);
        return states;
    }

    private static void VisitProjectElements(
        ProjectElementContainer parent,
        List<PropertyMutation> propertyMutations,
        ref int importVersion,
        Dictionary<string, PropertyMutationState> propertyMutationStates
    )
    {
        foreach (var child in parent.Children)
        {
            if (child is ProjectPropertyElement property)
            {
                propertyMutations.Add(new PropertyMutation(property.Name, GetConditionalScope(property)));
            }

            if (child is ProjectImportElement)
            {
                importVersion++;
            }

            if (child is ProjectTargetElement)
            {
                continue;
            }

            if (child is ProjectItemElement item)
            {
                propertyMutationStates[GetElementPath(item)] = new PropertyMutationState(
                    propertyMutations.ToArray(),
                    importVersion
                );
            }

            if (child is ProjectElementContainer container)
            {
                VisitProjectElements(container, propertyMutations, ref importVersion, propertyMutationStates);
            }
        }
    }

    private static HashSet<string> GetConditionPropertyNames(string? conditionalScope)
    {
        HashSet<string> propertyNames = new(StringComparer.OrdinalIgnoreCase);
        if (conditionalScope is null)
        {
            return propertyNames;
        }

        var index = 0;
        while (index + 2 < conditionalScope.Length)
        {
            if (conditionalScope[index] != '$' || conditionalScope[index + 1] != '(')
            {
                index++;
                continue;
            }

            var closingIndex = conditionalScope.IndexOf(')', index + 2);
            if (closingIndex <= index + 2)
            {
                index++;
                continue;
            }

            var propertyName = conditionalScope[(index + 2)..closingIndex];
            if (IsSimplePropertyName(propertyName))
            {
                propertyNames.Add(propertyName);
            }

            index = closingIndex + 1;
        }

        return propertyNames;
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
            var propertyMutationStatesByPath = GetPropertyMutationStates(projectRoot);
            List<PropertyMutationState> propertyMutationStates = [];

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

                    var propertyMutationState = propertyMutationStatesByPath.GetValueOrDefault(
                        GetElementPath(item)
                    ) ?? new PropertyMutationState([], 0);

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
                    var itemConditionalScope = GetConditionalScope(item);
                    var versionMetadataCondition = versionMetadata?.Condition;
                    var versionOverrideMetadataCondition = versionOverrideMetadata?.Condition;
                    var metadataConditionalScope = GetConditionalMetadataScope(
                        versionMetadata,
                        versionOverrideMetadata
                    );
                    var conditionalScope = CombineConditionalScopes(
                        itemConditionalScope,
                        metadataConditionalScope
                    );
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

                    var hasVersionMetadata =
                        versionMetadata is not null
                        // Presence matters here: VersionOverride="" explicitly clears inherited
                        // metadata even though its normalized value is empty.
                        || versionOverride is not null;
                    if (
                        isUpdate
                        && hasVersionMetadata
                        && IsGlobbedPackageSpecification(packageName)
                    )
                    {
                        // A globbed Update applies to items selected by MSBuild, not to a package whose
                        // literal ID is the glob. Without evaluating the full item graph, treating it as a
                        // package declaration creates fictitious findings; retain the known declarations
                        // and fail closed for this version-bearing Update.
                        continue;
                    }

                    if (ShouldSplitConditionedMetadata(versionMetadataCondition, versionOverrideMetadataCondition))
                    {
                        foreach (var expandedPackageName in ExpandPackageNames(packageName))
                        {
                            AddDeclaredPackageReference(
                                references,
                                expandedPackageName,
                                projectFilePath,
                                projectName,
                                version,
                                null,
                                versionMetadata is not null,
                                false,
                                isUpdate,
                                true,
                                CombineConditionalScopes(
                                    itemConditionalScope,
                                    versionMetadataCondition
                                ),
                                itemConditionalScope,
                                versionMetadataCondition,
                                null,
                                propertyMutationStates,
                                propertyMutationState
                            );
                            AddDeclaredPackageReference(
                                references,
                                expandedPackageName,
                                projectFilePath,
                                projectName,
                                string.Empty,
                                versionOverride,
                                false,
                                versionOverrideMetadata is not null,
                                isUpdate,
                                true,
                                CombineConditionalScopes(
                                    itemConditionalScope,
                                    versionOverrideMetadataCondition
                                ),
                                itemConditionalScope,
                                null,
                                versionOverrideMetadataCondition,
                                propertyMutationStates,
                                propertyMutationState,
                                false,
                                string.IsNullOrWhiteSpace(versionOverride),
                                false
                            );
                        }

                        continue;
                    }

                    // Metadata-only Updates change how an existing item is consumed, not which version
                    // it declares. Do not add one alongside an Include it amends, because declaration-based
                    // duplicate rules would call that a duplicate; retain a standalone one so casing and
                    // other declaration-based rules can still see the package name. Version rules filter
                    // its empty effective version from their comparisons.
                    foreach (var expandedPackageName in ExpandPackageNames(packageName))
                    {
                        var hasPriorReference = isUpdate
                            && references.Any(existing =>
                                string.Equals(
                                    existing.PackageName,
                                    expandedPackageName,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );
                        if (isUpdate && !hasVersionMetadata && hasPriorReference)
                        {
                            continue;
                        }

                        AddDeclaredPackageReference(
                            references,
                            expandedPackageName,
                            projectFilePath,
                            projectName,
                            version,
                            versionOverride,
                            versionMetadata is not null,
                            versionOverrideMetadata is not null,
                            isUpdate,
                            isConditional,
                            conditionalScope,
                            itemConditionalScope,
                            versionMetadataCondition,
                            versionOverrideMetadataCondition,
                            propertyMutationStates,
                            propertyMutationState
                        );
                    }
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

    private static IEnumerable<string> ExpandPackageNames(string packageName)
    {
        return packageName.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsGlobbedPackageSpecification(string packageName)
    {
        return packageName.Contains('*') || packageName.Contains('?');
    }

    private static void AddDeclaredPackageReference(
        List<PackageReference> references,
        string packageName,
        string projectFilePath,
        string projectName,
        string version,
        string? versionOverride,
        bool hasVersionMetadata,
        bool hasVersionOverrideMetadata,
        bool isUpdate,
        bool isConditional,
        string? conditionalScope,
        string? itemConditionalScope,
        string? versionMetadataCondition,
        string? versionOverrideMetadataCondition,
        List<PropertyMutationState> propertyMutationStates,
        PropertyMutationState propertyMutationState,
        bool allowUnconditionalMetadataProjection = true,
        bool allowInheritedVersion = true,
        bool allowInheritedVersionOverride = true
    )
    {
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
            HasVersionMetadata = hasVersionMetadata,
            HasVersionOverrideMetadata = hasVersionOverrideMetadata,
            HasConditionalUpdateVersionMetadata = isUpdate
                && isConditional
                && hasVersionMetadata,
            ConditionalScope = conditionalScope,
        };
        if (isUpdate)
        {
            reference = reference with
            {
                IsMetadataOnlyUpdate = !hasVersionMetadata && !hasVersionOverrideMetadata,
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
            conditionalScope,
            propertyMutationStates,
            propertyMutationState
        );

        if (amendedIndices.Count > 0)
        {
            ApplyAmendments(
                references,
                amendedIndices,
                propertyMutationStates,
                propertyMutationState,
                version,
                hasVersionMetadata,
                versionOverride?.Trim(),
                hasVersionOverrideMetadata,
                isConditional
            );

            return;
        }

        // A conditional Update can target an inherited item represented by an earlier
        // unconditional VersionOverride record. MSBuild retains that override when the Update
        // changes only Version, so carry it onto the conditional projection instead of
        // inventing a floating effective version for the branch. Resolve it only after checking
        // for a same-scope amendment so a newer conditional override is never overwritten by an
        // older unconditional one.
        var inheritedVersion = allowInheritedVersion
            ? FindInheritedVersion(
                references,
                propertyMutationStates,
                propertyMutationState,
                isUpdate,
                isConditional,
                hasVersionMetadata,
                packageName,
                conditionalScope
            )
            : null;
        var inheritedVersionOverride = allowInheritedVersionOverride
            ? FindInheritedVersionOverride(
                references,
                propertyMutationStates,
                propertyMutationState,
                isUpdate,
                isConditional,
                versionOverride,
                packageName,
                conditionalScope
            )
            : null;

        var effectiveReference = reference with
        {
            Version = inheritedVersion ?? reference.Version,
            VersionOverride = inheritedVersionOverride ?? reference.VersionOverride,
            HasVersionOverrideMetadata = inheritedVersionOverride is not null
                || reference.HasVersionOverrideMetadata,
        };
        if (allowUnconditionalMetadataProjection)
        {
            AddUnconditionalMetadataProjection(
                references,
                effectiveReference,
                itemConditionalScope,
                versionMetadataCondition,
                versionOverrideMetadataCondition,
                propertyMutationStates,
                propertyMutationState
            );
        }
        propertyMutationStates.Add(propertyMutationState);
        references.Add(effectiveReference);
    }

    private static void AddUnconditionalMetadataProjection(
        List<PackageReference> references,
        PackageReference conditionalReference,
        string? itemConditionalScope,
        string? versionMetadataCondition,
        string? versionOverrideMetadataCondition,
        List<PropertyMutationState> propertyMutationStates,
        PropertyMutationState propertyMutationState
    )
    {
        var hasConditionedVersion = !string.IsNullOrWhiteSpace(versionMetadataCondition);
        var hasConditionedOverride = !string.IsNullOrWhiteSpace(versionOverrideMetadataCondition);
        if (
            itemConditionalScope is not null
            || (!hasConditionedVersion && !hasConditionedOverride)
            || (
                !conditionalReference.HasVersionMetadata
                && !conditionalReference.HasVersionOverrideMetadata
            )
        )
        {
            return;
        }

        var hasUnconditionalVersion = conditionalReference.HasVersionMetadata && !hasConditionedVersion;
        var hasUnconditionalOverride = conditionalReference.HasVersionOverrideMetadata
            && !hasConditionedOverride;
        if (!hasUnconditionalVersion && !hasUnconditionalOverride)
        {
            if (
                itemConditionalScope is null
                && (
                    !conditionalReference.IsConditionalUpdate
                    || !references.Any(existing =>
                        string.Equals(
                            existing.PackageName,
                            conditionalReference.PackageName,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && !existing.IsConditional
                        && !existing.IsMetadataOnlyUpdate
                    )
                )
            )
            {
                references.Add(
                    conditionalReference with
                    {
                        Version = string.Empty,
                        HasVersionMetadata = false,
                        VersionOverride = null,
                        HasVersionOverrideMetadata = false,
                        HasConditionalUpdateVersionMetadata = false,
                        IsConditional = false,
                        IsConditionalUpdate = false,
                        IsMetadataOnlyUpdate = false,
                        ConditionalScope = null,
                    }
                );
                propertyMutationStates.Add(propertyMutationState);
            }

            return;
        }

        references.Add(
            conditionalReference with
            {
                Version = hasUnconditionalVersion ? conditionalReference.Version : string.Empty,
                HasVersionMetadata = hasUnconditionalVersion,
                VersionOverride = hasUnconditionalOverride
                    ? conditionalReference.VersionOverride
                    : null,
                HasVersionOverrideMetadata = hasUnconditionalOverride,
                HasConditionalUpdateVersionMetadata = false,
                IsConditional = false,
                IsConditionalUpdate = false,
                IsMetadataOnlyUpdate = false,
                ConditionalScope = null,
            }
        );
        propertyMutationStates.Add(propertyMutationState);
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
                    var metadataConditionalScope = GetConditionalMetadataScope(versionMetadata);
                    if (metadataConditionalScope is not null)
                    {
                        conditionalScope = conditionalScope is null
                            ? metadataConditionalScope
                            : $"{conditionalScope} -> {metadataConditionalScope}";
                    }
                    var isConditional = conditionalScope is not null;
                    foreach (var expandedPackageName in ExpandPackageNames(packageName))
                    {
                        var amendedIndices = FindAmendmentIndices(
                            references,
                            isUpdate,
                            isConditional,
                            expandedPackageName,
                            conditionalScope
                        );

                        if (IsExpandableVersion(versionMetadata.Value))
                        {
                            _logger.LogDebug(
                                "Skipping MSBuild variable version '{Version}' for package {Package} in {Project}",
                                versionMetadata.Value,
                                expandedPackageName,
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
                                null,
                                null,
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
                                    expandedPackageName,
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
        string? conditionalScope,
        IReadOnlyList<PropertyMutationState>? propertyMutationStates = null,
        PropertyMutationState? propertyMutationState = null
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
                            propertyMutationStates is null
                            || propertyMutationState is not null
                                && PropertyMutationStatesMatch(
                                    propertyMutationState,
                                    propertyMutationStates[item.index],
                                    conditionalScope,
                                    item.existing.ConditionalScope
                                )
                        )
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

    private static bool PropertyMutationStatesMatch(
        PropertyMutationState current,
        PropertyMutationState existing,
        string? currentScope,
        string? existingScope
    )
    {
        if (current.ImportVersion != existing.ImportVersion)
        {
            return false;
        }

        var propertyNames = GetConditionPropertyNames(currentScope);
        propertyNames.UnionWith(GetConditionPropertyNames(existingScope));
        if (current.Mutations.Count < existing.Mutations.Count)
        {
            return false;
        }

        for (var index = existing.Mutations.Count; index < current.Mutations.Count; index++)
        {
            var mutation = current.Mutations[index];
            if (
                propertyNames.Contains(mutation.PropertyName)
                && ConditionalScopesMayOverlap(mutation.ConditionalScope, currentScope)
                && ConditionalScopesMayOverlap(mutation.ConditionalScope, existingScope)
            )
            {
                return false;
            }
        }

        return true;
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
                AreMutuallyExclusiveMetadataConditions(leftCondition, rightCondition)
            )
        );
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
        var leftCondition = NormalizeConditionSyntax(left.Condition);
        var rightCondition = NormalizeConditionSyntax(right.Condition);
        var conditionsMatch = string.Equals(leftCondition, rightCondition, StringComparison.Ordinal)
            || (
                left.BranchPath is null
                && right.BranchPath is null
                && ConditionCovers(leftCondition, rightCondition)
            );
        return conditionsMatch
            && (
                allowExternalBranchMatch
                    ? left.BranchPath is null
                        || right.BranchPath is null
                        || ConditionalBranchPathsMatch(
                            leftCondition,
                            left.BranchPath,
                            right.BranchPath
                        )
                    : left.BranchPath is not null
                        && right.BranchPath is not null
                        && ConditionalBranchPathsMatch(
                            leftCondition,
                            left.BranchPath,
                            right.BranchPath
                        )
            );
    }

    private static bool ConditionCovers(string widerCondition, string narrowerCondition)
    {
        var wider = widerCondition.Trim();
        var narrower = narrowerCondition.Trim();
        while (HasEnclosingParentheses(wider))
        {
            wider = wider[1..^1].Trim();
        }

        while (HasEnclosingParentheses(narrower))
        {
            narrower = narrower[1..^1].Trim();
        }

        var widerDisjuncts = SplitTopLevelCondition(wider, "Or")
            .Select(NormalizeConditionDisjunct)
            .ToArray();
        var narrowerDisjuncts = SplitTopLevelCondition(narrower, "Or")
            .Select(NormalizeConditionDisjunct)
            .ToArray();
        if (
            widerDisjuncts
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(narrowerDisjuncts)
        )
        {
            return true;
        }

        return narrowerDisjuncts.All(narrowerDisjunct =>
            widerDisjuncts.Any(widerDisjunct =>
                ConditionDisjunctCovers(widerDisjunct, narrowerDisjunct)
            )
        );
    }

    private static bool ConditionDisjunctCovers(string widerDisjunct, string narrowerDisjunct)
    {
        if (string.Equals(widerDisjunct, narrowerDisjunct, StringComparison.Ordinal))
        {
            return true;
        }

        return TryGetConditionConjuncts(widerDisjunct, out var widerConjuncts)
            && TryGetConditionConjuncts(narrowerDisjunct, out var narrowerConjuncts)
            && narrowerConjuncts.IsSupersetOf(widerConjuncts);
    }

    private static string NormalizeConditionDisjunct(string disjunct)
    {
        var normalized = NormalizeConditionSyntax(disjunct.Trim());
        while (HasEnclosingParentheses(normalized))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    private static string NormalizeConditionSyntax(string condition)
    {
        var normalized = new StringBuilder(condition.Length);
        var inSingleQuotedLiteral = false;
        var inDoubleQuotedLiteral = false;
        for (var index = 0; index < condition.Length; index++)
        {
            var character = condition[index];
            if (character == '\'' && !inDoubleQuotedLiteral)
            {
                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                normalized.Append(character);
                continue;
            }

            if (character == '"' && !inSingleQuotedLiteral)
            {
                inDoubleQuotedLiteral = !inDoubleQuotedLiteral;
                normalized.Append(character);
                continue;
            }

            if (!char.IsWhiteSpace(character) || inSingleQuotedLiteral || inDoubleQuotedLiteral)
            {
                normalized.Append(character);
                continue;
            }

            var nextIndex = index + 1;
            while (nextIndex < condition.Length && char.IsWhiteSpace(condition[nextIndex]))
            {
                nextIndex++;
            }

            var previousCharacter = normalized.Length == 0 ? '\0' : normalized[^1];
            var nextCharacter = nextIndex < condition.Length ? condition[nextIndex] : '\0';
            if (
                IsConditionNormalizationBoundary(previousCharacter)
                || IsConditionNormalizationBoundary(nextCharacter)
            )
            {
                continue;
            }

            if (normalized.Length > 0 && normalized[^1] != ' ')
            {
                normalized.Append(' ');
            }
        }

        return NormalizePropertyNameCase(
            NormalizeSimpleEqualityLiteralCase(normalized.ToString().Trim())
        );
    }

    private static bool AreMutuallyExclusiveMetadataConditions(
        string leftCondition,
        string rightCondition
    )
    {
        if (
            !TryGetSimpleEqualityCondition(
                leftCondition,
                out var leftProperty,
                out var leftLiteral
            )
            || !TryGetSimpleEqualityCondition(
                rightCondition,
                out var rightProperty,
                out var rightLiteral
            )
        )
        {
            // Compound conditions may overlap even when their text differs. Keep both metadata values
            // together unless the two simple equality guards prove that no item can satisfy both.
            return false;
        }

        return string.Equals(leftProperty, rightProperty, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(leftLiteral, rightLiteral, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSplitConditionedMetadata(
        string? versionCondition,
        string? versionOverrideCondition
    )
    {
        if (
            string.IsNullOrWhiteSpace(versionCondition)
            || string.IsNullOrWhiteSpace(versionOverrideCondition)
        )
        {
            return false;
        }

        var normalizedVersionCondition = NormalizeConditionSyntax(versionCondition.Trim());
        var normalizedOverrideCondition = NormalizeConditionSyntax(versionOverrideCondition.Trim());
        if (string.Equals(normalizedVersionCondition, normalizedOverrideCondition, StringComparison.Ordinal))
        {
            return false;
        }

        if (
            AreMutuallyExclusiveMetadataConditions(
                versionCondition,
                versionOverrideCondition
            )
        )
        {
            return true;
        }

        if (
            !TryGetConditionConjuncts(normalizedVersionCondition, out var versionConjuncts)
            || !TryGetConditionConjuncts(normalizedOverrideCondition, out var versionOverrideConjuncts)
        )
        {
            // Coverage using unsupported boolean forms cannot be proven. Preserve a possible
            // Version-only branch rather than allowing override precedence to hide it.
            return true;
        }

        // If every guard required by VersionOverride is also required by Version, the override covers
        // every Version evaluation. Otherwise Version remains effective in at least a possible branch.
        return !versionConjuncts.IsSupersetOf(versionOverrideConjuncts);
    }

    private static bool TryGetConditionConjuncts(
        string condition,
        out HashSet<string> conjuncts
    )
    {
        conjuncts = new HashSet<string>(StringComparer.Ordinal);
        return AddConditionConjuncts(condition, conjuncts);
    }

    private static bool AddConditionConjuncts(
        string condition,
        HashSet<string> conjuncts
    )
    {
        var normalized = condition.Trim();
        while (HasEnclosingParentheses(normalized))
        {
            normalized = normalized[1..^1].Trim();
        }

        var parts = SplitTopLevelCondition(normalized, "And");
        foreach (var part in parts)
        {
            var conjunct = part.Trim();
            while (HasEnclosingParentheses(conjunct))
            {
                conjunct = conjunct[1..^1].Trim();
            }

            if (SplitTopLevelCondition(conjunct, "And").Count > 1)
            {
                if (!AddConditionConjuncts(conjunct, conjuncts))
                {
                    conjuncts.Clear();
                    return false;
                }

                continue;
            }

            if (
                string.IsNullOrEmpty(conjunct)
                || SplitTopLevelCondition(conjunct, "Or").Count > 1
            )
            {
                conjuncts.Clear();
                return false;
            }

            conjuncts.Add(conjunct);
        }

        return conjuncts.Count > 0;
    }

    private static List<string> SplitTopLevelCondition(string condition, string operatorText)
    {
        List<string> parts = [];
        var start = 0;
        var depth = 0;
        var inSingleQuotedLiteral = false;
        var inDoubleQuotedLiteral = false;
        var index = 0;
        while (index < condition.Length)
        {
            var character = condition[index];
            if (character == '\'' && !inDoubleQuotedLiteral)
            {
                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                index++;
                continue;
            }

            if (character == '"' && !inSingleQuotedLiteral)
            {
                inDoubleQuotedLiteral = !inDoubleQuotedLiteral;
                index++;
                continue;
            }

            if (inSingleQuotedLiteral || inDoubleQuotedLiteral)
            {
                index++;
                continue;
            }

            if (character == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (character == ')')
            {
                depth--;
                index++;
                continue;
            }

            if (
                depth == 0
                && index + operatorText.Length <= condition.Length
                && string.Equals(
                    condition.Substring(index, operatorText.Length),
                    operatorText,
                    StringComparison.OrdinalIgnoreCase
                )
                && (index == 0 || !char.IsLetterOrDigit(condition[index - 1]))
                && (
                    index + operatorText.Length == condition.Length
                    || !char.IsLetterOrDigit(condition[index + operatorText.Length])
                )
            )
            {
                parts.Add(condition[start..index]);
                start = index + operatorText.Length;
                index += operatorText.Length;
                continue;
            }

            index++;
        }

        parts.Add(condition[start..]);
        return parts;
    }

    private static bool TryGetSimpleEqualityCondition(
        string condition,
        out string property,
        out string literal
    )
    {
        property = string.Empty;
        literal = string.Empty;
        var normalized = NormalizeConditionSyntax(condition).Trim();
        while (HasEnclosingParentheses(normalized))
        {
            normalized = normalized[1..^1].Trim();
        }

        var equalityIndex = normalized.IndexOf("==", StringComparison.Ordinal);
        if (
            equalityIndex <= 0
            || normalized.IndexOf("==", equalityIndex + 2, StringComparison.Ordinal) >= 0
        )
        {
            return false;
        }

        var left = normalized[..equalityIndex].Trim();
        var right = normalized[(equalityIndex + 2)..].Trim();
        if (!TryGetQuotedToken(left, out var leftToken) || !TryGetQuotedToken(right, out var rightToken))
        {
            return false;
        }

        if (TryGetSimplePropertyName(leftToken, out property))
        {
            literal = rightToken;
            return IsSimpleConditionLiteral(literal);
        }

        if (TryGetSimplePropertyName(rightToken, out property))
        {
            literal = leftToken;
            return IsSimpleConditionLiteral(literal);
        }

        property = string.Empty;
        return false;
    }

    private static bool TryGetQuotedToken(string value, out string token)
    {
        token = string.Empty;
        if (value.Length < 2 || (value[0] != value[^1]) || (value[0] != '\'' && value[0] != '"'))
        {
            return false;
        }

        token = value[1..^1];
        return !token.Contains(value[0]);
    }

    private static bool TryGetSimplePropertyName(string value, out string property)
    {
        property = string.Empty;
        if (value.Length < 4 || !value.StartsWith("$(", StringComparison.Ordinal) || value[^1] != ')')
        {
            return false;
        }

        var candidate = value[2..^1];
        if (!IsSimplePropertyName(candidate))
        {
            return false;
        }

        property = candidate;
        return true;
    }

    private static string NormalizePropertyNameCase(string condition)
    {
        var normalized = new StringBuilder(condition.Length);
        var index = 0;
        while (index < condition.Length)
        {
            if (condition[index] == '$' && index + 1 < condition.Length && condition[index + 1] == '(')
            {
                var closingIndex = condition.IndexOf(')', index + 2);
                if (closingIndex > index + 2)
                {
                    var property = condition[(index + 2)..closingIndex];
                    if (IsSimplePropertyName(property))
                    {
                        normalized.Append("$(");
                        normalized.Append(property.ToUpperInvariant());
                        normalized.Append(')');
                        index = closingIndex;
                        index++;
                        continue;
                    }
                }
            }

            normalized.Append(condition[index]);
            index++;
        }

        return normalized.ToString();
    }

    private static bool IsSimplePropertyName(string value)
    {
        return !string.IsNullOrEmpty(value)
            && value.All(character =>
                char.IsLetterOrDigit(character) || character is '_' or '.' or '-'
            );
    }

    private static bool HasEnclosingParentheses(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return false;
        }

        var depth = 0;
        var inSingleQuotedLiteral = false;
        var inDoubleQuotedLiteral = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\'' && !inDoubleQuotedLiteral)
            {
                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                continue;
            }

            if (character == '"' && !inSingleQuotedLiteral)
            {
                inDoubleQuotedLiteral = !inDoubleQuotedLiteral;
                continue;
            }

            if (inSingleQuotedLiteral || inDoubleQuotedLiteral)
            {
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0 && index != value.Length - 1)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static string NormalizeSimpleEqualityLiteralCase(string condition)
    {
        var normalized = new StringBuilder(condition.Length);
        var inSingleQuotedLiteral = false;
        var inDoubleQuotedLiteral = false;
        var index = 0;
        while (index < condition.Length)
        {
            var character = condition[index];
            if (character == '\'' && !inDoubleQuotedLiteral)
            {
                var isClosingQuote = inSingleQuotedLiteral;
                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                normalized.Append(character);
                if (isClosingQuote && IsEqualityOperatorAfter(condition, index))
                {
                    CanonicalizeTrailingSimpleConditionLiteral(normalized);
                }

                index++;
                continue;
            }

            if (character == '"' && !inSingleQuotedLiteral)
            {
                var isClosingQuote = inDoubleQuotedLiteral;
                inDoubleQuotedLiteral = !inDoubleQuotedLiteral;
                normalized.Append(character);
                if (isClosingQuote && IsEqualityOperatorAfter(condition, index))
                {
                    CanonicalizeTrailingSimpleConditionLiteral(normalized);
                }

                index++;
                continue;
            }

            if (
                !inSingleQuotedLiteral
                && !inDoubleQuotedLiteral
                && IsEqualityOperatorAt(condition, index)
            )
            {
                normalized.Append(character);
                normalized.Append('=');
                index += 2;
                var literalStart = index;
                while (
                    literalStart < condition.Length
                    && char.IsWhiteSpace(condition[literalStart])
                )
                {
                    normalized.Append(condition[literalStart]);
                    literalStart++;
                }

                if (
                    TryReadSimpleConditionLiteral(
                        condition,
                        literalStart,
                        out var quote,
                        out var value,
                        out var literalEnd
                    )
                )
                {
                    normalized.Append(quote);
                    normalized.Append(value.ToUpperInvariant());
                    normalized.Append(quote);
                    index = literalEnd + 1;
                    continue;
                }

                index = literalStart;
                continue;
            }

            normalized.Append(character);
            index++;
        }

        return normalized.ToString();
    }

    private static bool IsEqualityOperatorAfter(string condition, int index)
    {
        var nextIndex = index + 1;
        while (nextIndex < condition.Length && char.IsWhiteSpace(condition[nextIndex]))
        {
            nextIndex++;
        }

        return IsEqualityOperatorAt(condition, nextIndex);
    }

    private static void CanonicalizeTrailingSimpleConditionLiteral(StringBuilder condition)
    {
        if (condition.Length < 2 || (condition[^1] != '\'' && condition[^1] != '"'))
        {
            return;
        }

        var quote = condition[^1];
        var openingQuote = condition.Length - 2;
        while (openingQuote >= 0 && condition[openingQuote] != quote)
        {
            openingQuote--;
        }

        if (openingQuote < 0)
        {
            return;
        }

        var value = condition.ToString(
            openingQuote + 1,
            condition.Length - openingQuote - 2
        );
        if (!IsSimpleConditionLiteral(value))
        {
            return;
        }

        condition.Remove(openingQuote + 1, value.Length);
        condition.Insert(openingQuote + 1, value.ToUpperInvariant());
    }

    private static bool IsEqualityOperatorAt(string condition, int index)
    {
        return index + 1 < condition.Length
            && (condition[index] == '=' || condition[index] == '!')
            && condition[index + 1] == '=';
    }

    private static bool TryReadSimpleConditionLiteral(
        string condition,
        int start,
        out char quote,
        out string value,
        out int end
    )
    {
        quote = '\0';
        value = string.Empty;
        end = start;
        if (start >= condition.Length || (condition[start] != '\'' && condition[start] != '"'))
        {
            return false;
        }

        quote = condition[start];
        var closingQuote = condition.IndexOf(quote, start + 1);
        if (closingQuote < 0)
        {
            return false;
        }

        value = condition[(start + 1)..closingQuote];
        if (!IsSimpleConditionLiteral(value))
        {
            value = string.Empty;
            return false;
        }

        end = closingQuote;
        return true;
    }

    private static bool IsSimpleConditionLiteral(string value)
    {
        return !string.IsNullOrEmpty(value)
            && value.All(character =>
                char.IsLetterOrDigit(character)
                || character is '.' or '-' or '_'
            );
    }

    private static bool IsConditionNormalizationBoundary(char character)
    {
        return character is '=' or '!' or '>' or '<' or '(' or ')';
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
                precedingWhenConditions.Add(NormalizeConditionSyntax(sibling.Condition.Trim()));
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
        IReadOnlyList<PropertyMutationState> propertyMutationStates,
        PropertyMutationState propertyMutationState,
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
            .Select((existing, index) => (existing, index))
            .Where(item =>
                IsInheritedReference(
                    item.existing,
                    propertyMutationStates[item.index],
                    propertyMutationState,
                    packageName,
                    conditionalScope
                )
                && !string.IsNullOrWhiteSpace(item.existing.Version)
            )
            .Select(item => item.existing.Version)
            .LastOrDefault();
    }

    private static string? FindInheritedVersionOverride(
        IReadOnlyList<PackageReference> references,
        IReadOnlyList<PropertyMutationState> propertyMutationStates,
        PropertyMutationState propertyMutationState,
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
            .Select((existing, index) => (existing, index))
            .Where(item =>
                IsInheritedReference(
                    item.existing,
                    propertyMutationStates[item.index],
                    propertyMutationState,
                    packageName,
                    conditionalScope
                )
                && !string.IsNullOrWhiteSpace(item.existing.VersionOverride)
            )
            .Select(item => item.existing.VersionOverride)
            .LastOrDefault();
    }

    private static bool IsInheritedReference(
        PackageReference existing,
        PropertyMutationState existingPropertyMutationState,
        PropertyMutationState propertyMutationState,
        string packageName,
        string? conditionalScope
    )
    {
        return (
            !existing.IsConditional
            || (
                PropertyMutationStatesMatch(
                    propertyMutationState,
                    existingPropertyMutationState,
                    conditionalScope,
                    existing.ConditionalScope
                )
                && IsWiderConditionalScope(existing.ConditionalScope, conditionalScope)
            )
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
        List<PropertyMutationState>? propertyMutationStates,
        PropertyMutationState? propertyMutationState,
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
            && versionOverride is null
            && IsExplicitVersionOverrideClear(references[index])
        );
        var unconditionalRecordTemplate = CanCreateUnconditionalRecordTemplate(
            isConditional,
            hasUnconditionalReference,
            hasConditionalReference,
            hasSurvivingConditionalClear,
            version,
            versionOverride,
            hasVersionOverrideMetadata
        )
            ? amendedIndices
                .Select(index => references[index])
                .FirstOrDefault(existing =>
                    IsUnconditionalRecordTemplate(existing, versionOverride, hasVersionOverrideMetadata)
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
            var foldedIndex = foldedIndices[foldedPosition];
            references.RemoveAt(foldedIndex);
            propertyMutationStates?.RemoveAt(foldedIndex);
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
            if (propertyMutationState is not null)
            {
                propertyMutationStates?.Add(propertyMutationState);
            }
        }
    }

    private static bool PropagateConditionalUpdateVersionMetadata(
        PackageReference existing,
        bool isConditional,
        bool hasVersionMetadata
    )
    {
        if (!isConditional && hasVersionMetadata)
        {
            return false;
        }

        return existing.HasConditionalUpdateVersionMetadata
            || (isConditional && hasVersionMetadata && existing.IsConditionalUpdate);
    }

    private static bool CanCreateUnconditionalRecordTemplate(
        bool isConditional,
        bool hasUnconditionalReference,
        bool hasConditionalReference,
        bool hasSurvivingConditionalClear,
        string version,
        string? versionOverride,
        bool hasVersionOverrideMetadata
    )
    {
        return !isConditional
            && !hasUnconditionalReference
            && hasConditionalReference
            && !hasSurvivingConditionalClear
            && (
                !string.IsNullOrWhiteSpace(version)
                || !string.IsNullOrWhiteSpace(versionOverride)
                || (hasVersionOverrideMetadata && versionOverride is not null)
            );
    }

    private static bool IsUnconditionalRecordTemplate(
        PackageReference existing,
        string? versionOverride,
        bool hasVersionOverrideMetadata
    )
    {
        return existing.IsConditional
            && existing.IsConditionalUpdate
            && (
                !IsExplicitVersionOverrideClear(existing)
                || (
                    existing.HasConditionalUpdateVersionMetadata
                    && !string.IsNullOrWhiteSpace(existing.Version)
                    && hasVersionOverrideMetadata
                    && versionOverride is not null
                )
            )
            && ConditionalUpdateMetadataSurvives(existing, versionOverride)
            && (
                !string.IsNullOrWhiteSpace(existing.VersionOverride)
                || !string.IsNullOrWhiteSpace(versionOverride)
                || (hasVersionOverrideMetadata && versionOverride is not null)
            );
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
