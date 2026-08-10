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
    private static bool HasConditionalAncestor(ProjectElement element)
    {
        for (ProjectElement? current = element; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Condition))
            {
                return true;
            }

            // <Otherwise> has no Condition of its own but applies exactly when no sibling <When> did.
            if (current is ProjectOtherwiseElement)
            {
                return true;
            }
        }

        return false;
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
                    var isConditional = HasConditionalAncestor(item);

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
                    );
                    if (isUpdate && !hasVersionMetadata)
                    {
                        reference = reference with { IsMetadataOnlyUpdate = true };
                    }

                    // An unconditional, version-bearing Update amends the item already declared rather
                    // than adding another one, so recording it separately would have RedundantReference
                    // report a duplicate that does not exist, and would leave the superseded version in
                    // the list for FloatingVersion to read. A conditional Update must stay separate: it
                    // does not make an unconditional Include conditional for every target framework. With
                    // no Include to amend it stands on its own: that is a project adjusting a reference it
                    // inherits. Metadata-only Updates were ignored above because they do not declare a
                    // version for these rules to compare.
                    var amendedIndices = FindUnconditionalAmendmentIndices(
                        references,
                        isUpdate,
                        isConditional,
                        packageName
                    );

                    if (
                        isUpdate
                        && !hasPriorReference
                        && HasLaterInclude(items, itemIndex, packageName)
                    )
                    {
                        continue;
                    }

                    if (amendedIndices.Count > 0)
                    {
                        foreach (var amendedIndex in amendedIndices)
                        {
                            references[amendedIndex] = references[amendedIndex] with
                            {
                                Version = string.IsNullOrWhiteSpace(version)
                                    ? references[amendedIndex].Version
                                    : version,
                                IsConditional =
                                    references[amendedIndex].IsConditional || reference.IsConditional,
                                VersionOverride =
                                    reference.VersionOverride ?? references[amendedIndex].VersionOverride,
                                IsMetadataOnlyUpdate = false,
                            };
                        }

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

                    var hasPriorReference = isUpdate
                        && references.Any(existing =>
                            string.Equals(
                                existing.PackageName,
                                packageName,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    var isConditional = HasConditionalAncestor(item);
                    var amendedIndices = FindUnconditionalAmendmentIndices(
                        references,
                        isUpdate,
                        isConditional,
                        packageName
                    );

                    if (
                        isUpdate
                        && !hasPriorReference
                        && HasLaterInclude(items, itemIndex, packageName)
                    )
                    {
                        continue;
                    }

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
                        foreach (var amendedIndex in amendedIndices)
                        {
                            references[amendedIndex] = references[amendedIndex] with
                            {
                                Version = versionMetadata.Value,
                            };
                        }
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

    private static bool HasLaterInclude(
        IReadOnlyList<ProjectItemElement> items,
        int currentIndex,
        string packageName
    )
    {
        for (var index = currentIndex + 1; index < items.Count; index++)
        {
            var item = items[index];
            if (
                item.ItemType == "PackageReference"
                && !HasConditionalAncestor(item)
                && string.Equals(item.Include, packageName, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static List<int> FindUnconditionalAmendmentIndices(
        IReadOnlyList<PackageReference> references,
        bool isUpdate,
        bool isConditional,
        string packageName
    )
    {
        if (!isUpdate || isConditional)
        {
            return [];
        }

        return references
            .Select((existing, index) => (existing, index))
            .Where(item =>
                !item.existing.IsConditional
                &&
                string.Equals(
                    item.existing.PackageName,
                    packageName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(item => item.index)
            .ToList();
    }
}
