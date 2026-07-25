using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// Applies an arbitrary subset of package updates to <c>Directory.Packages.props</c> and reverts back to
/// the pristine baseline on demand.
/// </summary>
/// <remarks>
/// Every <see cref="ApplyAsync"/> call rewrites the file from the captured baseline rather than layering
/// edits on top of the previous state. That independence is what lets a search strategy probe many
/// unrelated subsets without the results depending on the order they were tried in.
/// </remarks>
public interface IUpdateTransaction
{
    /// <summary>
    /// Rewrites the props file so exactly <paramref name="subset"/> is applied on top of the baseline.
    /// An empty subset is equivalent to <see cref="RevertAsync"/>.
    /// </summary>
    Task ApplyAsync(IReadOnlyCollection<PackageUpdateEntry> subset);

    /// <summary>
    /// Restores the props file to the exact bytes captured when the transaction began.
    /// </summary>
    Task RevertAsync();
}
