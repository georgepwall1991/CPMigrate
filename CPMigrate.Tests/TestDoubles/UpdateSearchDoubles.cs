using CPMigrate.Models;
using CPMigrate.Services.Update;

namespace CPMigrate.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IUpdateTransaction"/> that records what the strategy wrote, so tests can assert
/// the file ends up in the state the search reported.
/// </summary>
public sealed class RecordingUpdateTransaction : IUpdateTransaction
{
    private readonly List<IReadOnlyCollection<PackageUpdateEntry>> _applied = [];

    /// <summary>Every subset written, in order.</summary>
    public IReadOnlyList<IReadOnlyCollection<PackageUpdateEntry>> AppliedSubsets => _applied;

    /// <summary>Package names in the most recent write.</summary>
    public IEnumerable<string> LastAppliedNames =>
        _applied.Count == 0 ? [] : _applied[^1].Select(u => u.PackageName);

    public int RevertCount { get; private set; }

    public Task ApplyAsync(IReadOnlyCollection<PackageUpdateEntry> subset)
    {
        _applied.Add([.. subset]);
        return Task.CompletedTask;
    }

    public Task RevertAsync()
    {
        RevertCount++;
        _applied.Add([]);
        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="IVerificationRunner"/> driven by a predicate over the probed subset, with the same
/// memoization contract as the real runner so tests measure the same run counts production would see.
/// </summary>
public sealed class ScriptedVerificationRunner : IVerificationRunner
{
    private readonly Func<IReadOnlyCollection<PackageUpdateEntry>, bool> _passes;
    private readonly Dictionary<string, VerificationResult> _cache = new(StringComparer.Ordinal);

    public ScriptedVerificationRunner(Func<IReadOnlyCollection<PackageUpdateEntry>, bool> passes)
    {
        _passes = passes;
    }

    public int RunCount { get; private set; }

    /// <summary>Every subset the strategy asked about, including cache hits.</summary>
    public List<IReadOnlyCollection<PackageUpdateEntry>> ProbedSubsets { get; } = [];

    public Task<VerificationResult> VerifyAsync(IReadOnlyCollection<PackageUpdateEntry> subset)
    {
        ProbedSubsets.Add([.. subset]);

        var key = DotNetVerificationRunner.BuildCacheKey(subset);
        if (_cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        RunCount++;
        var result = _passes(subset)
            ? VerificationResult.Success()
            : new VerificationResult(VerificationOutcome.TestsFailed, "scripted failure");

        _cache[key] = result;
        return Task.FromResult(result);
    }
}
