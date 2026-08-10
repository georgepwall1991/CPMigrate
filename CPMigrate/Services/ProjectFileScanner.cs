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
                        ? $"{condition}@{GetElementPath(current)}"
                        : condition
                );
            }

            // <Otherwise> has no Condition of its own but applies exactly when no sibling <When> did.
            if (current is ProjectOtherwiseElement)
            {
                conditions.Add($"<Otherwise>@{GetElementPath(current)}");
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

                    var version =
                        item.Metadata.FirstOrDefault(m => m.Name == "Version")?.Value
                        ?? string.Empty;

                    // Carried separately from Version: under central package management this is the
                    // version actually in force, and it can float exactly as a Version can — but a
                    // rule asking "does this project pin inline?" must not read it as one, because
                    // VersionOverride is NuGet's sanctioned way to step outside the central pin.
                    var versionOverride = item
                        .Metadata.FirstOrDefault(m => m.Name == "VersionOverride")
                        ?.Value;

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
                        !string.IsNullOrWhiteSpace(version)
                        || !string.IsNullOrWhiteSpace(versionOverride);
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
                            reference.VersionOverride,
                            isConditional
                        );

                        continue;
                    }

                    references.Add(reference);
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

                    if (versionMetadata.Value.Contains("$("))
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
                            null,
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
                        && string.Equals(
                            item.existing.ConditionalScope,
                            conditionalScope,
                            StringComparison.Ordinal
                        )
                    )
                )
            )
            .Select(item => item.index)
            .ToList();
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
        string? versionOverride,
        bool isConditional
    )
    {
        var foldsConditionalUpdates =
            isConditional && amendedIndices.All(index => references[index].IsConditionalUpdate);
        var hasItemDeclaration = amendedIndices.Any(index => !references[index].IsConditionalUpdate);
        var unconditionalRecordTemplate =
            !isConditional && !hasItemDeclaration && !string.IsNullOrWhiteSpace(version)
                ? amendedIndices
                    .Select(index => references[index])
                    .FirstOrDefault(existing =>
                        ConditionalUpdateMetadataSurvives(existing, versionOverride)
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
                Version = string.IsNullOrWhiteSpace(version) ? existing.Version : version,
                IsConditional = foldsConditionalUpdates
                    || (existing.IsConditional && (!existing.IsConditionalUpdate || conditionalMetadataSurvives)),
                VersionOverride = versionOverride ?? existing.VersionOverride,
                IsMetadataOnlyUpdate = false,
                IsConditionalUpdate = foldsConditionalUpdates
                    || (existing.IsConditionalUpdate && conditionalMetadataSurvives),
            };
        }

        for (var foldedPosition = foldedIndices.Count - 1; foldedPosition >= 0; foldedPosition--)
        {
            references.RemoveAt(foldedIndices[foldedPosition]);
        }

        // A conditional VersionOverride can amend an inherited item without creating a local Include.
        // When a later unconditional Update supplies the ordinary version, retain both facts: the
        // conditional override for its target and an unconditional record for the base Update. Collapsing
        // them into the conditional record hides the base version from cross-project drift analysis.
        if (unconditionalRecordTemplate is not null)
        {
            references.Add(
                unconditionalRecordTemplate with
                {
                    Version = version,
                    IsConditional = false,
                    VersionOverride = versionOverride,
                    IsMetadataOnlyUpdate = false,
                    IsConditionalUpdate = false,
                    ConditionalScope = null,
                }
            );
        }
    }

    private static bool ConditionalUpdateMetadataSurvives(
        PackageReference existing,
        string? versionOverride
    )
    {
        return existing.IsConditionalUpdate
            && string.IsNullOrWhiteSpace(versionOverride)
            && !string.IsNullOrWhiteSpace(existing.VersionOverride);
    }
}
