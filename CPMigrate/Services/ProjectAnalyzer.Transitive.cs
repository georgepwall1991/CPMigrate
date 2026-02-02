using System.Text.Json;
using CPMigrate.Models;

namespace CPMigrate.Services;

public partial class ProjectAnalyzer
{
    /// <summary>
    /// Scans a project for resolved top-level package references using dotnet package list JSON output.
    /// This supports CPM-managed projects where versions are centralized in Directory.Packages.props.
    /// </summary>
    public async Task<(List<PackageReference> References, bool Success)> ScanResolvedPackagesAsync(
        string projectFilePath,
        bool includeTransitive = false)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions { IncludeTransitive = includeTransitive };
            var (output, success) = await _dotNetCliService.RunPackageListJsonAsync(projectFilePath, options);

            if (!success)
            {
                return (new List<PackageReference>(), false);
            }

            var references = ParsePackageReferencesFromJson(output, projectFilePath, projectName, includeTransitive);
            return (references, true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan packages for {projectName}: {ex.Message}");
            return (new List<PackageReference>(), false);
        }
    }

    /// <summary>
    /// Scans a project for transitive dependencies using 'dotnet list package --include-transitive'.
    /// Requires the project to be restored.
    /// </summary>
    public async Task<(List<PackageReference> References, bool Success)> ScanTransitivePackagesAsync(string projectFilePath)
    {
        var (references, success) = await ScanResolvedPackagesAsync(projectFilePath, includeTransitive: true);
        return (references.Where(r => r.IsTransitive).ToList(), success);
    }

    /// <summary>
    /// Scans a project for known vulnerabilities using 'dotnet list package --vulnerable --include-transitive'.
    /// </summary>
    public async Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(string projectFilePath)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions
            {
                IncludeTransitive = true,
                Vulnerable = true
            };

            var (output, success) = await _dotNetCliService.RunPackageListJsonAsync(projectFilePath, options);

            if (!success)
            {
                return (new List<VulnerabilityInfo>(), false);
            }

            var vulnerabilities = ParseVulnerabilitiesFromJson(output, projectName);
            return (vulnerabilities, true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan vulnerabilities for {projectName}: {ex.Message}");
            return (new List<VulnerabilityInfo>(), false);
        }
    }

    /// <summary>
    /// Scans a project for outdated packages using dotnet package list --outdated.
    /// </summary>
    public async Task<(List<OutdatedPackageInfo> Packages, bool Success)> ScanOutdatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions
            {
                IncludeTransitive = includeTransitive,
                Outdated = true,
                IncludePrerelease = includePrerelease
            };

            var (output, success) = await _dotNetCliService.RunPackageListJsonAsync(projectFilePath, options);
            if (!success)
            {
                return (new List<OutdatedPackageInfo>(), false);
            }

            return (ParseOutdatedPackagesFromJson(output, projectFilePath, projectName, includeTransitive), true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan outdated packages for {projectName}: {ex.Message}");
            return (new List<OutdatedPackageInfo>(), false);
        }
    }

    /// <summary>
    /// Scans a project for deprecated packages using dotnet package list --deprecated.
    /// </summary>
    public async Task<(List<DeprecatedPackageInfo> Packages, bool Success)> ScanDeprecatedPackagesAsync(
        string projectFilePath,
        bool includeTransitive,
        bool includePrerelease = false)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions
            {
                IncludeTransitive = includeTransitive,
                Deprecated = true,
                IncludePrerelease = includePrerelease
            };

            var (output, success) = await _dotNetCliService.RunPackageListJsonAsync(projectFilePath, options);
            if (!success)
            {
                return (new List<DeprecatedPackageInfo>(), false);
            }

            return (ParseDeprecatedPackagesFromJson(output, projectFilePath, projectName, includeTransitive), true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan deprecated packages for {projectName}: {ex.Message}");
            return (new List<DeprecatedPackageInfo>(), false);
        }
    }

    internal static List<PackageReference> ParsePackageReferencesFromJson(
        string output,
        string projectFilePath,
        string projectName,
        bool includeTransitive)
    {
        var references = new List<PackageReference>();
        using var doc = JsonDocument.Parse(output);

        if (!TryGetPropertyCaseInsensitive(doc.RootElement, "projects", out var projectsNode) || projectsNode.ValueKind != JsonValueKind.Array)
        {
            return references;
        }

        foreach (var project in projectsNode.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(project, "frameworks", out var frameworksNode) || frameworksNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var framework in frameworksNode.EnumerateArray())
            {
                AddPackageReferences(framework, "topLevelPackages", projectFilePath, projectName, references, isTransitive: false);

                if (includeTransitive)
                {
                    AddPackageReferences(framework, "transitivePackages", projectFilePath, projectName, references, isTransitive: true);
                }
            }
        }

        return references;
    }

    internal static List<VulnerabilityInfo> ParseVulnerabilitiesFromJson(string output, string projectName)
    {
        var vulnerabilities = new List<VulnerabilityInfo>();
        using var doc = JsonDocument.Parse(output);

        if (!TryGetPropertyCaseInsensitive(doc.RootElement, "projects", out var projectsNode) || projectsNode.ValueKind != JsonValueKind.Array)
        {
            return vulnerabilities;
        }

        foreach (var project in projectsNode.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(project, "frameworks", out var frameworksNode) || frameworksNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var framework in frameworksNode.EnumerateArray())
            {
                AddVulnerabilities(framework, "topLevelPackages", projectName, vulnerabilities);
                AddVulnerabilities(framework, "transitivePackages", projectName, vulnerabilities);
            }
        }

        return vulnerabilities;
    }

    internal static List<OutdatedPackageInfo> ParseOutdatedPackagesFromJson(
        string output,
        string projectFilePath,
        string projectName,
        bool includeTransitive)
    {
        var packages = new List<OutdatedPackageInfo>();
        using var doc = JsonDocument.Parse(output);

        if (!TryGetPropertyCaseInsensitive(doc.RootElement, "projects", out var projectsNode) || projectsNode.ValueKind != JsonValueKind.Array)
        {
            return packages;
        }

        foreach (var project in projectsNode.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(project, "frameworks", out var frameworksNode) || frameworksNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var framework in frameworksNode.EnumerateArray())
            {
                AddOutdatedPackages(framework, "topLevelPackages", projectFilePath, projectName, packages, isTransitive: false);
                if (includeTransitive)
                {
                    AddOutdatedPackages(framework, "transitivePackages", projectFilePath, projectName, packages, isTransitive: true);
                }
            }
        }

        return packages;
    }

    internal static List<DeprecatedPackageInfo> ParseDeprecatedPackagesFromJson(
        string output,
        string projectFilePath,
        string projectName,
        bool includeTransitive)
    {
        var packages = new List<DeprecatedPackageInfo>();
        using var doc = JsonDocument.Parse(output);

        if (!TryGetPropertyCaseInsensitive(doc.RootElement, "projects", out var projectsNode) || projectsNode.ValueKind != JsonValueKind.Array)
        {
            return packages;
        }

        foreach (var project in projectsNode.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(project, "frameworks", out var frameworksNode) || frameworksNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var framework in frameworksNode.EnumerateArray())
            {
                AddDeprecatedPackages(framework, "topLevelPackages", projectFilePath, projectName, packages, isTransitive: false);
                if (includeTransitive)
                {
                    AddDeprecatedPackages(framework, "transitivePackages", projectFilePath, projectName, packages, isTransitive: true);
                }
            }
        }

        return packages;
    }

    private static void AddPackageReferences(
        JsonElement frameworkNode,
        string propertyName,
        string projectFilePath,
        string projectName,
        List<PackageReference> references,
        bool isTransitive)
    {
        if (!TryGetPropertyCaseInsensitive(frameworkNode, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            var packageName = GetStringOrDefault(pkg, "id");
            var resolved = GetStringOrDefault(pkg, "resolvedVersion");

            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            references.Add(new PackageReference(packageName, resolved, projectFilePath, projectName, isTransitive));
        }
    }

    private static void AddVulnerabilities(
        JsonElement frameworkNode,
        string propertyName,
        string projectName,
        List<VulnerabilityInfo> vulnerabilities)
    {
        if (!TryGetPropertyCaseInsensitive(frameworkNode, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            var packageName = GetStringOrDefault(pkg, "id");
            var resolved = GetStringOrDefault(pkg, "resolvedVersion");
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            if (!TryGetPropertyCaseInsensitive(pkg, "vulnerabilities", out var vulnsNode) || vulnsNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var vuln in vulnsNode.EnumerateArray())
            {
                var fixedVersion = GetStringOrDefault(vuln, "fixedVersion");
                if (string.IsNullOrWhiteSpace(fixedVersion))
                {
                    fixedVersion = GetStringOrDefault(vuln, "firstPatchedVersion");
                }

                if (string.IsNullOrWhiteSpace(fixedVersion))
                {
                    fixedVersion = GetStringOrDefault(vuln, "patchedVersion");
                }

                if (string.IsNullOrWhiteSpace(fixedVersion))
                {
                    fixedVersion = GetStringOrDefault(vuln, "patchedVersions");
                }

                if (string.IsNullOrWhiteSpace(fixedVersion))
                {
                    fixedVersion = GetStringOrDefault(vuln, "recommendedVersion");
                }

                if (string.IsNullOrWhiteSpace(fixedVersion))
                {
                    fixedVersion = GetStringOrDefault(pkg, "latestVersion");
                }

                vulnerabilities.Add(new VulnerabilityInfo(
                    packageName,
                    GetStringOrDefault(vuln, "severity", "Unknown"),
                    GetAdvisoryId(vuln),
                    resolved,
                    fixedVersion,
                    projectName
                ));
            }
        }
    }

    private static void AddOutdatedPackages(
        JsonElement frameworkNode,
        string propertyName,
        string projectFilePath,
        string projectName,
        List<OutdatedPackageInfo> packages,
        bool isTransitive)
    {
        if (!TryGetPropertyCaseInsensitive(frameworkNode, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            var packageName = GetStringOrDefault(pkg, "id");
            var resolved = GetStringOrDefault(pkg, "resolvedVersion");
            var latest = GetStringOrDefault(pkg, "latestVersion");

            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(resolved) || string.IsNullOrWhiteSpace(latest))
            {
                continue;
            }

            packages.Add(new OutdatedPackageInfo(packageName, resolved, latest, projectFilePath, projectName, isTransitive));
        }
    }

    private static void AddDeprecatedPackages(
        JsonElement frameworkNode,
        string propertyName,
        string projectFilePath,
        string projectName,
        List<DeprecatedPackageInfo> packages,
        bool isTransitive)
    {
        if (!TryGetPropertyCaseInsensitive(frameworkNode, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            var packageName = GetStringOrDefault(pkg, "id");
            var resolved = GetStringOrDefault(pkg, "resolvedVersion");
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            var reasons = new List<string>();
            if (TryGetPropertyCaseInsensitive(pkg, "deprecationReasons", out var reasonsNode) && reasonsNode.ValueKind == JsonValueKind.Array)
            {
                reasons.AddRange(reasonsNode.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))!);
            }

            string? alternativePackage = null;
            string? alternativeVersionRange = null;
            if (TryGetPropertyCaseInsensitive(pkg, "alternativePackage", out var altNode) && altNode.ValueKind == JsonValueKind.Object)
            {
                alternativePackage = GetStringOrDefault(altNode, "id");
                alternativeVersionRange = GetStringOrDefault(altNode, "versionRange");
            }

            packages.Add(new DeprecatedPackageInfo(
                packageName,
                resolved,
                projectFilePath,
                projectName,
                reasons,
                alternativePackage,
                alternativeVersionRange,
                isTransitive));
        }
    }

    private static string GetStringOrDefault(JsonElement element, string propertyName, string defaultValue = "")
    {
        if (!TryGetPropertyCaseInsensitive(element, propertyName, out var property))
        {
            return defaultValue;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? defaultValue;
        }

        if (property.ValueKind == JsonValueKind.Number || property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            return property.GetRawText();
        }

        if (property.ValueKind == JsonValueKind.Object &&
            TryGetPropertyCaseInsensitive(property, "version", out var versionProperty) &&
            versionProperty.ValueKind == JsonValueKind.String)
        {
            return versionProperty.GetString() ?? defaultValue;
        }

        return defaultValue;
    }

    private static string GetAdvisoryId(JsonElement vulnerabilityNode)
    {
        var advisoryId = GetStringOrDefault(vulnerabilityNode, "advisoryurl");
        if (!string.IsNullOrWhiteSpace(advisoryId))
        {
            return advisoryId;
        }

        advisoryId = GetStringOrDefault(vulnerabilityNode, "advisoryUrl");
        if (!string.IsNullOrWhiteSpace(advisoryId))
        {
            return advisoryId;
        }

        advisoryId = GetStringOrDefault(vulnerabilityNode, "id");
        return advisoryId;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

#pragma warning disable S3267
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
#pragma warning restore S3267

        value = default;
        return false;
    }
}
