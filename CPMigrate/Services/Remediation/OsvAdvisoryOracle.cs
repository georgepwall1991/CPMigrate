using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services.Remediation;

/// <summary>
/// An <see cref="IAdvisoryOracle"/> backed by OSV.dev.
///
/// OSV is queried by advisory id, never by package — <c>GET /v1/vulns/{id}</c> rather than a search.
/// That is what keeps this from becoming a second vulnerability scanner: the id comes from the
/// advisory URL the .NET SDK already reported, so the join is exact and this service cannot surface
/// an advisory the SDK did not. Querying by package name and version instead would produce findings
/// of its own, with their own false positives, in a tool whose whole argument is that its verdicts
/// are the SDK's.
///
/// The caching and failure bookkeeping deliberately mirror
/// <see cref="NuGetVersionLookupService"/>, including storing the in-flight task so that concurrent
/// callers asking for the same advisory share one request.
/// </summary>
public sealed class OsvAdvisoryOracle : IAdvisoryOracle
{
#pragma warning disable S1075 // URIs should not be hardcoded - OSV.dev's public API is a stable URL
    private const string OsvVulnerabilityBaseUrl = "https://api.osv.dev/v1/vulns";
#pragma warning restore S1075

    private const string NuGetEcosystem = "NuGet";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<OsvAdvisoryOracle> _logger;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<double> _jitter;

    /// <summary>
    /// Advisory documents already fetched this run. A solution with the same vulnerable package in
    /// thirty projects reports the same advisory thirty times; this makes that one request.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<string?>> _cache = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ConcurrentDictionary<string, byte> _failedLookups = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Creates an oracle.
    /// </summary>
    /// <param name="httpClient">Client to use; one is created and owned when null.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="delay">Wait implementation, injected so retry tests do not sleep.</param>
    /// <param name="jitter">Jitter source in [0,1), injected so retry delays are deterministic in tests.</param>
    public OsvAdvisoryOracle(
        HttpClient? httpClient = null,
        ILogger<OsvAdvisoryOracle>? logger = null,
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
        _logger = logger ?? NullLogger<OsvAdvisoryOracle>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetFailedLookups()
    {
        return _failedLookups.Keys.ToList();
    }

    /// <inheritdoc />
    public async Task<AdvisoryRecord?> LookupAsync(string advisoryId, string packageId)
    {
        var id = ExtractAdvisoryId(advisoryId);

        if (string.IsNullOrEmpty(id))
        {
            // Nothing to query. Recorded as a failure rather than shrugged off: the caller asked
            // about a real finding and is getting no answer, which must not read as "no advisory".
            RecordFailure(advisoryId, "no advisory identifier could be read from the finding");
            return null;
        }

        var body = await FetchAsync(id);

        if (body == null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ParseAdvisory(document.RootElement, id, packageId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed OSV response for advisory {AdvisoryId}", id);
            RecordFailure(id, "malformed response");
            return null;
        }
    }

    /// <summary>
    /// Reads the advisory identifier out of whatever the SDK reported.
    ///
    /// <c>dotnet list package --vulnerable</c> emits an advisory URL
    /// (<c>https://github.com/advisories/GHSA-...</c>), never a bare id, so the last path segment is
    /// the identifier. A value that is already an id passes through unchanged.
    /// </summary>
    /// <param name="advisoryIdOrUrl">The advisory id or URL.</param>
    /// <returns>The identifier, or an empty string when none can be read.</returns>
    internal static string ExtractAdvisoryId(string advisoryIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(advisoryIdOrUrl))
        {
            return string.Empty;
        }

        var trimmed = advisoryIdOrUrl.Trim().TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];

        // Guard against a URL whose tail is a query string or an unrelated word: an id OSV can answer
        // for is always a prefixed, hyphenated token such as GHSA-xxxx-xxxx-xxxx or CVE-2024-21907.
        if (lastSegment.Length == 0 || lastSegment.Contains('?') || !lastSegment.Contains('-'))
        {
            return string.Empty;
        }

        return lastSegment;
    }

    private async Task<string?> FetchAsync(string advisoryId)
    {
        var lookup = _cache.GetOrAdd(advisoryId, FetchWithRetryAsync);
        var body = await lookup;

        if (_failedLookups.ContainsKey(advisoryId))
        {
            // A transient failure must not become this run's settled view of the advisory.
            _cache.TryRemove(advisoryId, out _);
        }

        return body;
    }

    private async Task<string?> FetchWithRetryAsync(string advisoryId)
    {
        var url = $"{OsvVulnerabilityBaseUrl}/{Uri.EscapeDataString(advisoryId)}";

        for (var attempt = 1; attempt <= NuGetRetryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Definitive: OSV does not carry this advisory. Not a transport failure, so it is
                    // not recorded as one — but the caller still gets null and will report that no
                    // fix version could be computed.
                    _logger.LogDebug("Advisory {AdvisoryId} is not in the OSV database", advisoryId);
                    ClearFailure(advisoryId);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (
                        !NuGetRetryPolicy.IsTransient(response.StatusCode)
                        || attempt == NuGetRetryPolicy.MaxAttempts
                    )
                    {
                        RecordFailure(advisoryId, $"HTTP {(int)response.StatusCode}");
                        return null;
                    }

                    await WaitBeforeRetryAsync(attempt, response.Headers.RetryAfter?.Delta, advisoryId);
                    continue;
                }

                ClearFailure(advisoryId);
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (NuGetRetryPolicy.IsTransient(ex, cancellationRequested: false))
            {
                if (attempt == NuGetRetryPolicy.MaxAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to fetch advisory {AdvisoryId} after {Attempts} attempts",
                        advisoryId,
                        attempt
                    );
                    RecordFailure(advisoryId, ex.GetType().Name);
                    return null;
                }

                await WaitBeforeRetryAsync(attempt, retryAfter: null, advisoryId);
            }
        }

        return null;
    }

    /// <summary>
    /// Turns an OSV vulnerability document into the version windows that matter for one NuGet package.
    /// </summary>
    /// <param name="root">The OSV document root.</param>
    /// <param name="advisoryId">The identifier the document was fetched under.</param>
    /// <param name="packageId">The NuGet package being remediated.</param>
    /// <returns>The advisory, or null when it says nothing about this package.</returns>
    internal static AdvisoryRecord? ParseAdvisory(JsonElement root, string advisoryId, string packageId)
    {
        var id = root.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? advisoryId : advisoryId;
        var aliases = ReadStringArray(root, "aliases");
        var severity = ReadSeverity(root);

        var ranges = new List<AdvisoryVersionRange>();
        var explicitVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("affected", out var affected) && affected.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in affected.EnumerateArray())
            {
                if (!DescribesPackage(entry, packageId))
                {
                    // An advisory can span ecosystems and sibling packages. Folding another package's
                    // ranges in here would compute a fix version from versions that do not exist.
                    continue;
                }

                var entryRanges = ReadRanges(entry);

                if (entryRanges is null)
                {
                    return null;
                }

                ranges.AddRange(entryRanges);

                foreach (var version in ReadStringArray(entry, "versions"))
                {
                    explicitVersions.Add(version);
                }
            }
        }

        if (ranges.Count == 0 && explicitVersions.Count == 0)
        {
            // The document exists but says nothing about this package's versions, so it cannot be used
            // to prove any version safe.
            return null;
        }

        return new AdvisoryRecord(id, aliases, severity, ranges, explicitVersions);
    }

    private static bool DescribesPackage(JsonElement affectedEntry, string packageId)
    {
        if (!affectedEntry.TryGetProperty("package", out var package))
        {
            return false;
        }

        var ecosystem = package.TryGetProperty("ecosystem", out var eco) ? eco.GetString() : null;
        var name = package.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;

        return string.Equals(ecosystem, NuGetEcosystem, StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, packageId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the usable ranges out of one <c>affected</c> entry.
    /// </summary>
    /// <returns>
    /// The ranges, or null when the entry carries one this code cannot evaluate. Null is not "no
    /// ranges": the caller turns it into a refusal to answer, because the alternative is worse in
    /// both directions. A dropped <c>fixed</c> event leaves a range that opens and never closes, so
    /// every version reads as vulnerable and a package with a published fix is reported unfixable;
    /// a dropped <c>introduced</c> does the reverse and can present an affected version as the fix.
    /// </returns>
    private static List<AdvisoryVersionRange>? ReadRanges(JsonElement affectedEntry)
    {
        var result = new List<AdvisoryVersionRange>();

        if (!affectedEntry.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var range in ranges.EnumerateArray())
        {
            // GIT ranges express boundaries as commit hashes, which carry no version ordering at all.
            // They sit alongside ECOSYSTEM ranges on the same advisory, so skipping them is normal
            // rather than exceptional -- the ECOSYSTEM range is the one that describes NuGet.
            var rangeType = range.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;

            if (
                rangeType is not null
                && !string.Equals(rangeType, "ECOSYSTEM", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rangeType, "SEMVER", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (!range.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var boundaries = new List<AdvisoryBoundary>();

            foreach (var evt in events.EnumerateArray())
            {
                if (!TryReadEvent(evt, out var boundary))
                {
                    // An event this parser does not understand inside a range it does. Refusing the
                    // whole advisory is the only safe reading.
                    return null;
                }

                if (boundary.HasValue)
                {
                    boundaries.Add(boundary.Value);
                }
            }

            if (boundaries.Count > 0)
            {
                result.Add(new AdvisoryVersionRange(boundaries));
            }
        }

        return result;
    }

    /// <summary>
    /// Reads one OSV range event.
    /// </summary>
    /// <param name="evt">The event object.</param>
    /// <param name="boundary">
    /// The boundary it denotes, or null for an event that is not a boundary at all (a <c>limit</c>
    /// marker bounds how far the range was evaluated).
    /// </param>
    /// <returns>False when the event is a boundary this parser cannot read, which invalidates the range.</returns>
    private static bool TryReadEvent(JsonElement evt, out AdvisoryBoundary? boundary)
    {
        boundary = null;

        if (evt.TryGetProperty("introduced", out var introduced))
        {
            var raw = introduced.GetString();

            // "0" is OSV's way of saying "every version up to the first fix". NuGetVersion parses
            // it, but spelling the intent out keeps the zero-version boundary from looking accidental.
            if (raw == "0")
            {
                boundary = new AdvisoryBoundary(new NuGetVersion(0, 0, 0), AdvisoryBoundaryKind.Introduced);
                return true;
            }

            if (NuGetVersion.TryParse(raw, out var introducedVersion))
            {
                boundary = new AdvisoryBoundary(introducedVersion, AdvisoryBoundaryKind.Introduced);
                return true;
            }

            return false;
        }

        if (evt.TryGetProperty("fixed", out var fixedNode))
        {
            if (!NuGetVersion.TryParse(fixedNode.GetString(), out var fixedVersion))
            {
                return false;
            }

            boundary = new AdvisoryBoundary(fixedVersion, AdvisoryBoundaryKind.Fixed);
            return true;
        }

        if (evt.TryGetProperty("last_affected", out var lastNode))
        {
            if (!NuGetVersion.TryParse(lastNode.GetString(), out var lastAffected))
            {
                return false;
            }

            // Carried as an inclusive boundary rather than converted to an exclusive one a patch
            // higher. That approximation is wrong wherever a release sits between the two: stable
            // 1.2.3 is already above 1.2.3-beta, and 1.2.3.1 is above 1.2.3.0, so closing at 1.2.4
            // would mark safe releases as affected and hide the real minimum fix.
            boundary = new AdvisoryBoundary(lastAffected, AdvisoryBoundaryKind.LastAffected);
            return true;
        }

        if (evt.TryGetProperty("limit", out _))
        {
            // A limit marker bounds how far the range was evaluated; it is not a boundary of the
            // vulnerable window, so it is skipped rather than treated as unreadable.
            return true;
        }

        return false;
    }

    private static string? ReadSeverity(JsonElement root)
    {
        if (
            root.TryGetProperty("database_specific", out var databaseSpecific)
            && databaseSpecific.ValueKind == JsonValueKind.Object
            && databaseSpecific.TryGetProperty("severity", out var severity)
        )
        {
            return severity.GetString();
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array
            .EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();
    }

    private async Task WaitBeforeRetryAsync(int attempt, TimeSpan? retryAfter, string advisoryId)
    {
        var wait = NuGetRetryPolicy.GetDelay(attempt, retryAfter, _jitter());
        _logger.LogDebug(
            "Retrying advisory {AdvisoryId} in {Delay}ms (attempt {Attempt})",
            advisoryId,
            wait.TotalMilliseconds,
            attempt + 1
        );

        await _delay(wait);
    }

    private void RecordFailure(string advisoryId, string reason)
    {
        _failedLookups.TryAdd(advisoryId, 0);
        _logger.LogWarning("Advisory lookup for {AdvisoryId} failed: {Reason}", advisoryId, reason);
    }

    private void ClearFailure(string advisoryId)
    {
        _failedLookups.TryRemove(advisoryId, out _);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
