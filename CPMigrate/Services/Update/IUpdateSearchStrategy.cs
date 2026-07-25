using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// Decides which of the accepted package updates end up applied, given a transaction that can write any
/// subset and a runner that can verify one.
/// </summary>
/// <remarks>
/// Implementations must leave the props file in the state described by
/// <see cref="UpdateSearchResult.Applied"/> when they return, including on the failure path.
/// </remarks>
public interface IUpdateSearchStrategy
{
    /// <summary>
    /// Searches for a set of updates that verifies cleanly.
    /// </summary>
    Task<UpdateSearchResult> SearchAsync(
        IReadOnlyList<PackageUpdateEntry> candidates,
        IUpdateTransaction transaction,
        IVerificationRunner runner);
}
