using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Queries the NuGet flat container API for latest package versions.
///
/// Two behaviours matter more than the query itself. First, a transient failure used to be
/// indistinguishable from "no newer version" — the method returned null either way — so a 503 or a
/// timeout silently reported a package as up to date, and a run could skip half a solution's updates
/// without saying anything. Failures are now retried, and whatever still fails is recorded so the
/// caller can report it. Second, results are cached per run: a solution referencing the same package
/// from thirty projects previously issued thirty identical requests.
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
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<double> _jitter;

    /// <summary>
    /// Versions already fetched this run, so a package referenced from thirty projects costs one
    /// request. Cached per instance rather than statically: a long-lived cache would serve stale
    /// versions, and the lifetime of a CLI invocation is exactly the window where staleness is
    /// impossible.
    ///
    /// Keyed on the in-flight <see cref="Task{TResult}"/> rather than the result, because callers run
    /// up to eight lookups concurrently — storing the task means two projects asking for the same
    /// package at the same moment share one request instead of racing to make two.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<List<NuGetVersion>?>> _cache = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Packages that could not be checked. A concurrent dictionary rather than a HashSet: the
    /// concurrent lookups write to this too, and an unsynchronised set can corrupt or throw.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _failedLookups = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetFailedLookups()
    {
        return _failedLookups.Keys.ToList();
    }

    /// <summary>
    /// Creates a lookup service.
    /// </summary>
    /// <param name="httpClient">Client to use; one is created and owned when null.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="delay">Wait implementation, injected so retry tests do not sleep.</param>
    /// <param name="jitter">Jitter source in [0,1), injected so retry delays are deterministic in tests.</param>
    public NuGetVersionLookupService(
        HttpClient? httpClient = null,
        ILogger<NuGetVersionLookupService>? logger = null,
        Func<TimeSpan, Task>? delay = null,
        Func<double>? jitter = null
    )
    {
        _delay = delay ?? Task.Delay;
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
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
        var lookup = _cache.GetOrAdd(packageId, FetchWithRetryAsync);
        var versions = await lookup;

        if (_failedLookups.ContainsKey(packageId))
        {
            // A transient failure must not become this run's settled view of the package: drop the
            // cached task so a later caller retries rather than inheriting the failure.
            _cache.TryRemove(packageId, out _);
        }

        return versions;
    }

    private async Task<List<NuGetVersion>?> FetchWithRetryAsync(string packageId)
    {
        var url = $"{NuGetFlatContainerBaseUrl}/{packageId.ToLowerInvariant()}/index.json";

        for (var attempt = 1; attempt <= NuGetRetryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Definitive: the package does not exist. Not a failure to report, and not worth
                    // three attempts.
                    _logger.LogDebug("Package {PackageId} not found on the feed", packageId);
                    ClearFailure(packageId);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (
                        !NuGetRetryPolicy.IsTransient(response.StatusCode)
                        || attempt == NuGetRetryPolicy.MaxAttempts
                    )
                    {
                        RecordFailure(packageId, $"HTTP {(int)response.StatusCode}");
                        return null;
                    }

                    await WaitBeforeRetryAsync(attempt, response.Headers.RetryAfter?.Delta, packageId);
                    continue;
                }

                var versions = ParseVersions(await response.Content.ReadAsStringAsync());

                if (versions is null)
                {
                    // Valid JSON, but not a version index. Caching that as definitive would let the
                    // package be reported as cleanly checked — the silent-incomplete result this all
                    // exists to prevent.
                    RecordFailure(packageId, "response contained no version list");
                    return null;
                }

                // An earlier attempt for this package may have exhausted its retries; it has now
                // recovered, so the caller must not still be told it could not be checked.
                ClearFailure(packageId);
                return versions;
            }
            catch (JsonException ex)
            {
                // Malformed body: retrying will not make it parse.
                _logger.LogWarning(ex, "Malformed version index for package {PackageId}", packageId);
                RecordFailure(packageId, "malformed response");
                return null;
            }
            catch (Exception ex) when (NuGetRetryPolicy.IsTransient(ex, cancellationRequested: false))
            {
                if (attempt == NuGetRetryPolicy.MaxAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to fetch versions for package {PackageId} after {Attempts} attempts",
                        packageId,
                        attempt
                    );
                    RecordFailure(packageId, ex.GetType().Name);
                    return null;
                }

                await WaitBeforeRetryAsync(attempt, retryAfter: null, packageId);
            }
        }

        return null;
    }

    private async Task WaitBeforeRetryAsync(int attempt, TimeSpan? retryAfter, string packageId)
    {
        var wait = NuGetRetryPolicy.GetDelay(attempt, retryAfter, _jitter());
        _logger.LogDebug(
            "Retrying {PackageId} in {Delay}ms (attempt {Attempt})",
            packageId,
            wait.TotalMilliseconds,
            attempt + 1
        );

        await _delay(wait);
    }

    /// <summary>
    /// Records that a package could not be checked. Without this the caller cannot tell "already up
    /// to date" from "never managed to ask", which is the difference between a clean run and a
    /// silently incomplete one.
    /// </summary>
    private void RecordFailure(string packageId, string reason)
    {
        _failedLookups.TryAdd(packageId, 0);
        _logger.LogWarning("Version lookup for {PackageId} failed: {Reason}", packageId, reason);
    }

    /// <summary>
    /// Forgets a previously recorded failure, so a package that recovered is not still reported as
    /// unchecked.
    /// </summary>
    private void ClearFailure(string packageId)
    {
        _failedLookups.TryRemove(packageId, out _);
    }

    private static List<NuGetVersion>? ParseVersions(string body)
    {
        using var doc = JsonDocument.Parse(body);

        if (
            !doc.RootElement.TryGetProperty("versions", out var versionsProp)
            || versionsProp.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }

        return versionsProp
            .EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => v != null)
            .Select(v => NuGetVersion.TryParse(v!, out var parsed) ? parsed : null)
            .Where(v => v != null)
            .Cast<NuGetVersion>()
            .OrderByDescending(v => v)
            .ToList();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
