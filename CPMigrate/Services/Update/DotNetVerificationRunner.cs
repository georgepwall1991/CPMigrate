using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// Verifies an applied update subset by shelling out to <c>dotnet restore</c> and <c>dotnet test</c>.
/// </summary>
/// <remarks>
/// Results are memoized by subset identity. Bisection revisits the same subset whenever a probe fails and
/// the search falls back to a previously banked state, and a test suite is by far the most expensive thing
/// in the loop — so repeating one is pure waste.
/// </remarks>
public sealed class DotNetVerificationRunner : IVerificationRunner
{
    private readonly IDotNetCliService _dotNetCli;
    private readonly IConsoleService _console;
    private readonly string _targetPath;
    private readonly string? _testFilter;
    private readonly Dictionary<string, VerificationResult> _cache = new(StringComparer.Ordinal);

    public DotNetVerificationRunner(
        IDotNetCliService dotNetCli,
        IConsoleService console,
        string targetPath,
        string? testFilter = null)
    {
        _dotNetCli = dotNetCli;
        _console = console;
        _targetPath = targetPath;
        _testFilter = string.IsNullOrWhiteSpace(testFilter) ? null : testFilter;
    }

    /// <inheritdoc />
    public int RunCount { get; private set; }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(IReadOnlyCollection<PackageUpdateEntry> subset)
    {
        var key = BuildCacheKey(subset);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        RunCount++;

        _console.Info("Running dotnet restore...");
        var (restoreOutput, restoreSuccess) = await _dotNetCli.RunRestoreAsync(_targetPath);
        if (!restoreSuccess)
        {
            return Remember(key, new VerificationResult(VerificationOutcome.RestoreFailed, restoreOutput));
        }

        _console.Info("Running dotnet test...");
        var (testOutput, testSuccess) = await _dotNetCli.RunTestAsync(_targetPath, _testFilter);

        var result = testSuccess
            ? VerificationResult.Success()
            : new VerificationResult(VerificationOutcome.TestsFailed, testOutput);

        return Remember(key, result);
    }

    private VerificationResult Remember(string key, VerificationResult result)
    {
        _cache[key] = result;
        return result;
    }

    /// <summary>
    /// Builds an order-independent identity for a subset. Two subsets holding the same package/version
    /// pairs produce the same props file, so they must share a cache entry regardless of enumeration order.
    /// </summary>
    internal static string BuildCacheKey(IReadOnlyCollection<PackageUpdateEntry> subset)
    {
        if (subset.Count == 0)
        {
            return "<baseline>";
        }

        return string.Join(
            "|",
            subset
                .Select(u => $"{u.PackageName}@{u.LatestVersion}")
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
    }
}
