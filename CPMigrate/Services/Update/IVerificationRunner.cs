using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// Verifies that the currently applied set of package updates restores and tests cleanly.
/// </summary>
public interface IVerificationRunner
{
    /// <summary>
    /// Number of restore+test cycles actually executed. Cache hits do not count.
    /// </summary>
    int RunCount { get; }

    /// <summary>
    /// Runs <c>dotnet restore</c> followed by <c>dotnet test</c> against the current working tree.
    /// </summary>
    /// <param name="subset">
    /// The subset currently written to disk. Used only as a memoization key — the runner never applies it.
    /// Callers must apply the subset before calling this.
    /// </param>
    Task<VerificationResult> VerifyAsync(IReadOnlyCollection<PackageUpdateEntry> subset);
}
