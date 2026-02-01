using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Queries the NuGet flat container API for latest package versions.
/// </summary>
public sealed class NuGetVersionLookupService : INuGetVersionLookupService
{
#pragma warning disable S1075 // URIs should not be hardcoded - NuGet public API is a stable URL
    private const string NuGetFlatContainerBaseUrl = "https://api.nuget.org/v3-flatcontainer";
#pragma warning restore S1075

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<NuGetVersionLookupService> _logger;

    public NuGetVersionLookupService(HttpClient? httpClient = null, ILogger<NuGetVersionLookupService>? logger = null)
    {
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        if (_ownsHttpClient)
        {
            _httpClient.Timeout = DefaultTimeout;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "CPMigrate-CLI");
        }
        _logger = logger ?? NullLogger<NuGetVersionLookupService>.Instance;
    }

    /// <inheritdoc />
    public async Task<NuGetVersion?> GetLatestVersionAsync(string packageId, bool includePrerelease = false)
    {
        var versions = await FetchVersionsAsync(packageId);
        if (versions == null || versions.Count == 0)
        {
            return null;
        }

        if (includePrerelease)
        {
            return versions[0]; // Already sorted descending
        }

        // Don't fall back to prerelease when user didn't opt in
        return versions.FirstOrDefault(v => !v.IsPrerelease);
    }

    /// <inheritdoc />
    public async Task<NuGetVersion?> GetLatestVersionInMajorAsync(string packageId, int majorVersion, bool includePrerelease = false)
    {
        var versions = await FetchVersionsAsync(packageId);
        if (versions == null || versions.Count == 0)
        {
            return null;
        }

        var filtered = versions.Where(v => v.Major == majorVersion);

        if (!includePrerelease)
        {
            filtered = filtered.Where(v => !v.IsPrerelease);
        }

        return filtered.FirstOrDefault();
    }

    private async Task<List<NuGetVersion>?> FetchVersionsAsync(string packageId)
    {
        try
        {
            var url = $"{NuGetFlatContainerBaseUrl}/{packageId.ToLowerInvariant()}/index.json";
            var response = await _httpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("versions", out var versionsProp) &&
                versionsProp.ValueKind == JsonValueKind.Array)
            {
                return versionsProp.EnumerateArray()
                    .Select(v => v.GetString())
                    .Where(v => v != null)
                    .Select(v => NuGetVersion.TryParse(v!, out var parsed) ? parsed : null)
                    .Where(v => v != null)
                    .Cast<NuGetVersion>()
                    .OrderByDescending(v => v)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch versions for package {PackageId}", packageId);
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
