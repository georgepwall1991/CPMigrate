using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// The default strategy: apply every accepted update, verify once, and revert everything if that fails.
/// </summary>
public sealed class AllOrNothingSearchStrategy : IUpdateSearchStrategy
{
    /// <inheritdoc />
    public async Task<UpdateSearchResult> SearchAsync(
        IReadOnlyList<PackageUpdateEntry> candidates,
        IUpdateTransaction transaction,
        IVerificationRunner runner)
    {
        await transaction.ApplyAsync(candidates);
        var verification = await runner.VerifyAsync(candidates);

        if (verification.Passed)
        {
            return new UpdateSearchResult(candidates, [], runner.RunCount);
        }

        await transaction.RevertAsync();
        return new UpdateSearchResult(
            [],
            candidates.Select(c => c with { HeldBack = true }).ToList(),
            runner.RunCount,
            FailureOutput: verification.Output);
    }
}
