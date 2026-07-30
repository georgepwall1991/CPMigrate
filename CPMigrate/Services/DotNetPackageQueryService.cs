using System.Text.Json;
using CPMigrate.Models;
namespace CPMigrate.Services;

public sealed class DotNetPackageQueryService : IDotNetPackageQueryService
{
    private readonly IConsoleService _consoleService;
    private readonly IDotNetCliService _dotNetCliService;

    public DotNetPackageQueryService(
        IConsoleService consoleService,
        IDotNetCliService? dotNetCliService = null)
    {
        _consoleService = consoleService;
        _dotNetCliService = dotNetCliService ?? new DotNetCliService();
    }

    public async Task<(List<PackageReference> References, bool Success)> ScanResolvedPackagesAsync(
        string projectFilePath,
        bool includeTransitive = false,
        string? isolatedIntermediateDirectory = null)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions
            {
                IncludeTransitive = includeTransitive,
                IsolatedIntermediateDirectory = isolatedIntermediateDirectory,
            };
            var (output, success) = await _dotNetCliService.RunPackageListJsonAsync(projectFilePath, options);

            if (!success)
            {
                return ([], false);
            }

            return (ParsePackageReferencesFromJson(output, projectFilePath, projectName, includeTransitive), true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan packages for {projectName}: {ex.Message}");
            return ([], false);
        }
    }

    public async Task<(List<PackageReference> References, bool Success)> ScanTransitivePackagesAsync(string projectFilePath)
    {
        var (references, success) = await ScanResolvedPackagesAsync(projectFilePath, includeTransitive: true);
        return (references.Where(r => r.IsTransitive).ToList(), success);
    }

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
                return ([], false);
            }

            return (ParseVulnerabilitiesFromJson(output, projectName, projectFilePath), true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan vulnerabilities for {projectName}: {ex.Message}");
            return ([], false);
        }
    }

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
                return ([], false);
            }

            return (ParseOutdatedPackagesFromJson(output, projectFilePath, projectName, includeTransitive), true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan outdated packages for {projectName}: {ex.Message}");
            return ([], false);
        }
    }

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
                return ([], false);
            }

            return (ParseDeprecatedPackagesFromJson(output, projectFilePath, projectName, includeTransitive), true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan deprecated packages for {projectName}: {ex.Message}");
            return ([], false);
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

    internal static List<VulnerabilityInfo> ParseVulnerabilitiesFromJson(
        string output,
        string projectName,
        string projectFilePath = ""
    )
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
                AddVulnerabilities(
                    framework,
                    "topLevelPackages",
                    projectName,
                    projectFilePath,
                    vulnerabilities
                );
                AddVulnerabilities(
                    framework,
                    "transitivePackages",
                    projectName,
                    projectFilePath,
                    vulnerabilities
                );
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

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        var property = element.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (property.Value.ValueKind != JsonValueKind.Undefined)
        {
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static void AddPackageReferences(
        JsonElement framework,
        string propertyName,
        string projectFilePath,
        string projectName,
        List<PackageReference> references,
        bool isTransitive)
    {
        if (!TryGetPropertyCaseInsensitive(framework, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            var packageName = TryGetStringCaseInsensitive(pkg, "id") ?? TryGetStringCaseInsensitive(pkg, "name");
            var version = TryGetStringCaseInsensitive(pkg, "resolvedVersion");

            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            references.Add(new PackageReference(packageName, version, projectFilePath, projectName, isTransitive));
        }
    }

    private static void AddVulnerabilities(
        JsonElement framework,
        string propertyName,
        string projectName,
        string projectFilePath,
        List<VulnerabilityInfo> vulnerabilities)
    {
        if (!TryGetPropertyCaseInsensitive(framework, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(pkg, "vulnerabilities", out var vulnerabilitiesNode) ||
                vulnerabilitiesNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var packageName = TryGetStringCaseInsensitive(pkg, "id")
                ?? TryGetStringCaseInsensitive(pkg, "name")
                ?? string.Empty;
            var resolvedVersion = TryGetStringCaseInsensitive(pkg, "resolvedVersion") ?? string.Empty;

            foreach (var vulnerability in vulnerabilitiesNode.EnumerateArray())
            {
                var severity = TryGetStringCaseInsensitive(vulnerability, "severity") ?? "Unknown";
                var advisoryUrl = TryGetStringCaseInsensitive(vulnerability, "advisoryurl")
                    ?? TryGetStringCaseInsensitive(vulnerability, "advisoryUrl")
                    ?? string.Empty;
                var fixedVersion = GetFixedVersion(vulnerability, pkg);

                vulnerabilities.Add(new VulnerabilityInfo(
                    packageName,
                    severity,
                    advisoryUrl,
                    resolvedVersion,
                    fixedVersion,
                    projectName,
                    projectFilePath));
            }
        }
    }

    private static void AddOutdatedPackages(
        JsonElement framework,
        string propertyName,
        string projectFilePath,
        string projectName,
        List<OutdatedPackageInfo> packages,
        bool isTransitive)
    {
        if (!TryGetPropertyCaseInsensitive(framework, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            var latestVersion = TryGetStringCaseInsensitive(pkg, "latestVersion");
            var resolvedVersion = TryGetStringCaseInsensitive(pkg, "resolvedVersion");
            var packageName = TryGetStringCaseInsensitive(pkg, "id")
                ?? TryGetStringCaseInsensitive(pkg, "name");

            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(resolvedVersion) || string.IsNullOrWhiteSpace(latestVersion))
            {
                continue;
            }

            packages.Add(new OutdatedPackageInfo(packageName, resolvedVersion, latestVersion, projectFilePath, projectName, isTransitive));
        }
    }

    private static string GetFixedVersion(JsonElement vulnerability, JsonElement package)
    {
        var fixedVersion = TryGetStringOrFirstArrayEntryCaseInsensitive(vulnerability, "fixedversion")
            ?? TryGetStringOrFirstArrayEntryCaseInsensitive(vulnerability, "fixedVersion")
            ?? TryGetStringOrFirstArrayEntryCaseInsensitive(vulnerability, "firstPatchedVersion")
            ?? TryGetStringOrFirstArrayEntryCaseInsensitive(vulnerability, "patchedVersion")
            ?? TryGetStringOrFirstArrayEntryCaseInsensitive(vulnerability, "patchedVersions")
            ?? TryGetStringOrFirstArrayEntryCaseInsensitive(vulnerability, "recommendedVersion")
            ?? TryGetStringCaseInsensitive(package, "latestVersion");

        return fixedVersion ?? string.Empty;
    }

    private static string? TryGetStringOrFirstArrayEntryCaseInsensitive(JsonElement element, string name)
    {
        if (!TryGetPropertyCaseInsensitive(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => value.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString())
                .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry)),
            _ => null
        };
    }

    private static void AddDeprecatedPackages(
        JsonElement framework,
        string propertyName,
        string projectFilePath,
        string projectName,
        List<DeprecatedPackageInfo> packages,
        bool isTransitive)
    {
        if (!TryGetPropertyCaseInsensitive(framework, propertyName, out var packagesNode) || packagesNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pkg in packagesNode.EnumerateArray())
        {
            var packageName = TryGetStringCaseInsensitive(pkg, "id")
                ?? TryGetStringCaseInsensitive(pkg, "name");
            var resolvedVersion = TryGetStringCaseInsensitive(pkg, "resolvedVersion");
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(resolvedVersion))
            {
                continue;
            }

            var reasons = new List<string>();
            string? alternativePackage = null;
            string? alternativeVersionRange = null;

            if (TryGetPropertyCaseInsensitive(pkg, "deprecationReasons", out var directReasonsNode) &&
                directReasonsNode.ValueKind == JsonValueKind.Array)
            {
                reasons.AddRange(directReasonsNode.EnumerateArray()
                    .Select(r => r.GetString())
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Cast<string>());
            }

            if (TryGetPropertyCaseInsensitive(pkg, "alternativePackage", out var directAltPackageNode) &&
                directAltPackageNode.ValueKind == JsonValueKind.Object)
            {
                alternativePackage = TryGetStringCaseInsensitive(directAltPackageNode, "id")
                    ?? TryGetStringCaseInsensitive(directAltPackageNode, "name");
                alternativeVersionRange = TryGetStringCaseInsensitive(directAltPackageNode, "versionRange");
            }

            if (TryGetPropertyCaseInsensitive(pkg, "deprecations", out var deprecationsNode) &&
                deprecationsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var deprecation in deprecationsNode.EnumerateArray())
                {
                    if (TryGetPropertyCaseInsensitive(deprecation, "reasons", out var reasonsNode) && reasonsNode.ValueKind == JsonValueKind.Array)
                    {
                        reasons.AddRange(reasonsNode.EnumerateArray()
                            .Select(r => r.GetString())
                            .Where(r => !string.IsNullOrWhiteSpace(r))
                            .Cast<string>());
                    }

                    if (TryGetPropertyCaseInsensitive(deprecation, "alternatePackage", out var altPackageNode) && altPackageNode.ValueKind == JsonValueKind.Object)
                    {
                        alternativePackage ??= TryGetStringCaseInsensitive(altPackageNode, "id")
                            ?? TryGetStringCaseInsensitive(altPackageNode, "name");
                        alternativeVersionRange ??= TryGetStringCaseInsensitive(altPackageNode, "versionRange");
                    }
                }
            }

            if (reasons.Count == 0 && string.IsNullOrWhiteSpace(alternativePackage) && string.IsNullOrWhiteSpace(alternativeVersionRange))
            {
                continue;
            }

            packages.Add(new DeprecatedPackageInfo(
                packageName,
                resolvedVersion,
                projectFilePath,
                projectName,
                reasons.Distinct().ToList(),
                alternativePackage,
                alternativeVersionRange,
                isTransitive));
        }
    }

    private static string? TryGetStringCaseInsensitive(JsonElement element, string name)
    {
        return TryGetPropertyCaseInsensitive(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
