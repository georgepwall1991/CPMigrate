using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// Finds the largest subset of updates that still restores and tests cleanly, instead of discarding every
/// update when one of them breaks the build.
/// </summary>
/// <remarks>
/// <para>
/// This is delta debugging over the accepted update set, not a plain binary search. Plain binary search
/// assumes a single independent culprit; real dependency failures often need two packages together (a
/// library and its own updated dependency), so each probe is verified against the set already banked as
/// good rather than in isolation.
/// </para>
/// <para>
/// Whole set first, so a healthy update run costs exactly one verification and pays no bisection overhead.
/// Only when that fails does the set get halved. A half that verifies clean is banked and becomes part of
/// the baseline every later probe builds on; a half that fails is split again until it is a single package,
/// which is then held back.
/// </para>
/// <para>
/// Expect roughly <c>2·log₂(n)</c> verification runs for a single culprit, more when culprits interact.
/// The run budget bounds that: once it is spent, whatever is still queued is held back and the banked
/// good set is applied, so an interrupted search still delivers partial progress.
/// </para>
/// </remarks>
public sealed class BisectSearchStrategy : IUpdateSearchStrategy
{
    /// <summary>Default ceiling on restore+test cycles for one bisection.</summary>
    public const int DefaultBudget = 16;

    private readonly int _budget;
    private readonly IConsoleService _console;

    public BisectSearchStrategy(IConsoleService console, int budget = DefaultBudget)
    {
        _console = console;
        _budget = Math.Max(1, budget);
    }

    /// <inheritdoc />
    public async Task<UpdateSearchResult> SearchAsync(
        IReadOnlyList<PackageUpdateEntry> candidates,
        IUpdateTransaction transaction,
        IVerificationRunner runner)
    {
        if (candidates.Count == 0)
        {
            return new UpdateSearchResult([], [], runner.RunCount);
        }

        var good = new List<PackageUpdateEntry>();
        var held = new List<PackageUpdateEntry>();
        var pending = new Queue<List<PackageUpdateEntry>>();
        pending.Enqueue([.. candidates]);

        string? lastFailure = null;
        var budgetExhausted = false;

        while (pending.Count > 0)
        {
            if (runner.RunCount >= _budget)
            {
                budgetExhausted = true;
                break;
            }

            var chunk = pending.Dequeue();
            var probe = good.Concat(chunk).ToList();

            await transaction.ApplyAsync(probe);
            var verification = await runner.VerifyAsync(probe);

            if (verification.Passed)
            {
                good = probe;
                continue;
            }

            lastFailure = verification.Output;

            if (chunk.Count == 1)
            {
                held.Add(chunk[0]);
                ReportHeld(chunk[0], verification);
                continue;
            }

            var mid = chunk.Count / 2;
            pending.Enqueue(chunk.GetRange(0, mid));
            pending.Enqueue(chunk.GetRange(mid, chunk.Count - mid));
            _console.Dim($"  {chunk.Count} update(s) failed together — narrowing...");
        }

        // Whatever the budget cut short was never cleared, so it cannot be kept.
        while (pending.Count > 0)
        {
            held.AddRange(pending.Dequeue());
        }

        var baselineBroken = await DetectBrokenBaselineAsync(good, held, budgetExhausted, transaction, runner);

        await transaction.ApplyAsync(good);

        return new UpdateSearchResult(
            good,
            held.Select(h => h with { HeldBack = true }).ToList(),
            runner.RunCount,
            budgetExhausted,
            baselineBroken,
            lastFailure);
    }

    /// <summary>
    /// When nothing at all could be kept, checks whether the pre-existing tree already fails. Without this
    /// the user is told their packages are at fault when the real problem is a red baseline.
    /// </summary>
    private async Task<bool> DetectBrokenBaselineAsync(
        List<PackageUpdateEntry> good,
        List<PackageUpdateEntry> held,
        bool budgetExhausted,
        IUpdateTransaction transaction,
        IVerificationRunner runner)
    {
        if (good.Count > 0 || held.Count == 0 || budgetExhausted || runner.RunCount >= _budget)
        {
            return false;
        }

        _console.Dim("  No update could be kept — checking whether the baseline itself is green...");
        await transaction.RevertAsync();
        var baseline = await runner.VerifyAsync([]);

        if (!baseline.Passed)
        {
            _console.Warning("Verification fails with zero updates applied. The existing tree is already broken.");
            return true;
        }

        return false;
    }

    private void ReportHeld(PackageUpdateEntry entry, VerificationResult verification)
    {
        var reason = verification.Outcome == VerificationOutcome.RestoreFailed ? "restore failed" : "tests failed";
        _console.Warning($"  Holding back {entry.PackageName} {entry.CurrentVersion} → {entry.LatestVersion} ({reason}).");
    }
}
