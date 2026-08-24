using System.Collections.Concurrent;
using System.Text.Json;
using CPMigrate.Analyzers;
using CPMigrate.Models;
namespace CPMigrate.Services;

public sealed class DotNetPackageQueryService : IDotNetPackageQueryService
{
    private readonly IConsoleService _consoleService;
    private readonly IDotNetCliService _dotNetCliService;

    /// <summary>
    /// One <c>dotnet list package</c> invocation per project per run, shared by every caller that asks
    /// about resolved and/or transitive packages. Each subprocess costs seconds of restore-dependent
    /// latency, and a single run routinely asks both questions about the same project. The cached payload
    /// records which shape it answers: a payload fetched without <c>--include-transitive</c> carries no
    /// Transitive Packages section, so a transitive question triggers one upgrade fetch rather than being
    /// answered from data that cannot support it. Vulnerable/outdated/deprecated queries are different
    /// CLI commands with feed-dependent output and are never served from this cache.
    /// </summary>
    private readonly ConcurrentDictionary<
        (string ProjectPath, StringComparer Comparison, string IsolatedDirectory),
        Lazy<Task<PlainListPayload>>
    > _plainListCache = new();

    private sealed record PlainListPayload(bool Success, string Output, bool IncludesTransitive);

    /// <summary>
    /// Drops every cached list payload. Called when files have been rewritten mid-run (the fixer pass),
    /// so the next scan reads reality rather than the pre-fix restore output.
    /// </summary>
    public void ClearCache() => _plainListCache.Clear();

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
            var payload = await GetOrRunPlainListAsync(projectFilePath, isolatedIntermediateDirectory, includeTransitive);

            if (!payload.Success)
            {
                return ([], false);
            }

            // A project that reports no frameworks at all did not resolve, whatever its exit code says. That
            // is distinguishable from a project with no packages, which reports its frameworks with empty
            // package lists — checked against a real package-free project, not assumed.
            //
            // This mattered more than it looks. When a restore was broken, every caller downstream saw a
            // successful scan that happened to find nothing, so the project vanished from the report with
            // `scanFailures: 0` and no warning. That is how 3.26.0 silently lost three of Serilog's six
            // projects for a whole release: the breakage was visible in the output all along, and nothing
            // was looking at it.
            if (!DescribesAnyFramework(payload.Output))
            {
                _consoleService.Warning(
                    $"Could not scan packages for {projectName}: the project reported no frameworks, "
                        + "which means its restore did not complete."
                );

                return ([], false);
            }

            return (ParsePackageReferencesFromJson(payload.Output, projectFilePath, projectName, includeTransitive), true);
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

    /// <summary>
    /// Returns the cached <c>dotnet list package</c> payload for the project, invoking the CLI only when
    /// no cached payload exists or the cached one cannot answer a transitive question (it was fetched
    /// without <c>--include-transitive</c>, so it has no Transitive Packages section to parse).
    /// Failures and exceptions are never cached: restore and feed state can change between attempts
    /// within a run, so every caller retries exactly as it did before this cache existed.
    /// </summary>
    private async Task<PlainListPayload> GetOrRunPlainListAsync(
        string projectFilePath,
        string? isolatedIntermediateDirectory,
        bool includeTransitive)
    {
        // Path identity follows the filesystem: case-folded where the volume folds names
        // (default Windows/macOS, so Api.csproj and API.csproj share one payload) and verbatim
        // where it does not (/repo/tools/App.csproj and /repo/Tools/App.csproj stay distinct).
        var comparer = CpmDriftAnalyzer.PathComparerFor(Path.GetDirectoryName(projectFilePath));
        var pathKey = comparer == StringComparer.OrdinalIgnoreCase
            ? projectFilePath.ToUpperInvariant()
            : projectFilePath;
        // The mode rides in the key: an uppercased insensitive path and a verbatim sensitive path
        // could otherwise spell the identical tuple while meaning different projects.
        var key = (pathKey, comparer, isolatedIntermediateDirectory ?? string.Empty);
        while (true)
        {
            var lazy = _plainListCache.GetOrAdd(
                key,
                _ => new Lazy<Task<PlainListPayload>>(
                    () => RunPlainListCoreAsync(projectFilePath, isolatedIntermediateDirectory, includeTransitive)));

            PlainListPayload payload;
            try
            {
                payload = await lazy.Value;
            }
            catch
            {
                // A faulted invocation must not serve later callers; they re-run the query and emit
                // their own warning, exactly as they did before the cache existed.
                RemoveIfCurrent(key, lazy);
                throw;
            }

            if (!payload.Success || !DescribesAnyFramework(payload.Output))
            {
                // A CLI failure or an output with no frameworks is not a usable answer: restore
                // can recover between attempts within a run, so the next caller re-runs the query.
                RemoveIfCurrent(key, lazy);
                return payload;
            }
            if (!includeTransitive || payload.IncludesTransitive)
            {
                return payload;
            }

            // The cached output came from a plain `dotnet list package` run and carries no Transitive
            // Packages section, so a transitive answer needs its own invocation.
            RemoveIfCurrent(key, lazy);
        }
    }

    private async Task<PlainListPayload> RunPlainListCoreAsync(
        string projectFilePath,
        string? isolatedIntermediateDirectory,
        bool includeTransitive)
    {
        var options = new DotNetPackageListOptions
        {
            IncludeTransitive = includeTransitive,
            IsolatedIntermediateDirectory = isolatedIntermediateDirectory,
        };

        var (output, success) = await _dotNetCliService.RunPackageListJsonAsync(projectFilePath, options);
        return new PlainListPayload(success, output, includeTransitive);
    }
    private void RemoveIfCurrent(
        (string ProjectPath, StringComparer Comparison, string IsolatedDirectory) key,
        Lazy<Task<PlainListPayload>> lazy)
    {
        ((ICollection<KeyValuePair<(string ProjectPath, StringComparer Comparison, string IsolatedDirectory), Lazy<Task<PlainListPayload>>>>)_plainListCache)
            .Remove(KeyValuePair.Create(key, lazy));
    }

    public async Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(
        string projectFilePath,
        string? isolatedIntermediateDirectory = null)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions
            {
                IncludeTransitive = true,
                Vulnerable = true,
                IsolatedIntermediateDirectory = isolatedIntermediateDirectory
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
        bool includePrerelease = false,
        string? isolatedIntermediateDirectory = null)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions
            {
                IncludeTransitive = includeTransitive,
                Outdated = true,
                IncludePrerelease = includePrerelease,
                IsolatedIntermediateDirectory = isolatedIntermediateDirectory
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
        bool includePrerelease = false,
        string? isolatedIntermediateDirectory = null)
    {
        var projectName = Path.GetFileName(projectFilePath);

        try
        {
            var options = new DotNetPackageListOptions
            {
                IncludeTransitive = includeTransitive,
                Deprecated = true,
                IncludePrerelease = includePrerelease,
                IsolatedIntermediateDirectory = isolatedIntermediateDirectory
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


    /// <summary>
    /// Whether the output describes at least one framework for at least one project.
    ///
    /// The signal that a restore actually produced something. A project with no packages still reports its
    /// frameworks, with empty package lists — so an absent or empty frameworks array means the query failed
    /// rather than that there was nothing to find.
    /// </summary>
    internal static bool DescribesAnyFramework(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);

            if (
                !TryGetPropertyCaseInsensitive(doc.RootElement, "projects", out var projectsNode)
                || projectsNode.ValueKind != JsonValueKind.Array
            )
            {
                return false;
            }

            foreach (var project in projectsNode.EnumerateArray())
            {
                if (
                    TryGetPropertyCaseInsensitive(project, "frameworks", out var frameworks)
                    && frameworks.ValueKind == JsonValueKind.Array
                    && frameworks.GetArrayLength() > 0
                )
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            // Unparseable output is handled by the existing parse path, which reports it properly. Saying
            // "no frameworks" here would replace a specific error with a vaguer one.
            return true;
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
