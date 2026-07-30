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

    public ProjectFileScanner(IConsoleService consoleService, ILogger<ProjectFileScanner>? logger = null)
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

            var targetFramework = projectRoot.Properties
                .FirstOrDefault(p => p.Name == "TargetFramework" || p.Name == "TargetFrameworks")?.Value ?? "Unknown";

            return targetFramework;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            return "Unknown";
        }
    }

    public string ProcessProject(
        string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false)
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
        for (
            ProjectElement? current = element;
            current is not null;
            current = current.Parent
        )
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
                foreach (var item in projectRoot.Items)
                {
                    if (item.ItemType != "PackageReference")
                    {
                        continue;
                    }

                    var version =
                        item.Metadata.FirstOrDefault(m => m.Name == "Version")?.Value ?? string.Empty;

                    // Kept rather than filtered, because "this package is declared twice, both times
                    // conditionally" is a different fact from "this package is declared twice" and only
                    // the caller knows which one it needs.
                    var isConditional = HasConditionalAncestor(item);

                    references.Add(
                        new PackageReference(
                            item.Include,
                            version,
                            projectFilePath,
                            projectName,
                            IsTransitive: false,
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
            _logger.LogDebug(ex, "Could not read declarations from {Project}", projectName);
            return ([], false);
        }
    }

    public (List<PackageReference> References, bool Success) ScanProjectPackages(string projectFilePath)
    {
        var references = new List<PackageReference>();
        var projectName = Path.GetFileName(projectFilePath);

        try
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

                    if (versionMetadata.Value.Contains("$("))
                    {
                        _logger.LogDebug("Skipping MSBuild variable version '{Version}' for package {Package} in {Project}",
                            versionMetadata.Value, item.Include, projectName);
                        continue;
                    }

                    references.Add(new PackageReference(
                        item.Include,
                        versionMetadata.Value,
                        projectFilePath,
                        projectName));
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
}

