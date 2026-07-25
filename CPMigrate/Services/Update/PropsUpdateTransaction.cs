using CPMigrate.Models;

namespace CPMigrate.Services.Update;

/// <summary>
/// <see cref="IUpdateTransaction"/> backed by <c>Directory.Packages.props</c>.
/// </summary>
/// <remarks>
/// The pristine file content is captured in memory when the transaction is created, so revert works even
/// when the user passed <c>--no-backup</c>. On-disk backups remain a separate durability concern owned by
/// <see cref="BackupManager"/>; this type only guarantees in-process reversibility.
/// </remarks>
public sealed class PropsUpdateTransaction : IUpdateTransaction
{
    private readonly string _propsPath;
    private readonly string _baselineContent;
    private readonly Dictionary<string, HashSet<string>> _baselineVersions;
    private readonly PropsGenerator _propsGenerator;

    private PropsUpdateTransaction(
        string propsPath,
        string baselineContent,
        Dictionary<string, HashSet<string>> baselineVersions,
        PropsGenerator propsGenerator)
    {
        _propsPath = propsPath;
        _baselineContent = baselineContent;
        _baselineVersions = baselineVersions;
        _propsGenerator = propsGenerator;
    }

    /// <summary>
    /// Captures the current props file as the baseline all later applies are computed from.
    /// </summary>
    public static async Task<PropsUpdateTransaction> BeginAsync(
        string propsPath,
        Dictionary<string, HashSet<string>> baselineVersions,
        PropsGenerator propsGenerator)
    {
        var content = await File.ReadAllTextAsync(propsPath);
        // Copy the baseline dictionary so a caller mutating theirs cannot corrupt our revert target.
        var versions = new Dictionary<string, HashSet<string>>(baselineVersions, StringComparer.OrdinalIgnoreCase);
        return new PropsUpdateTransaction(propsPath, content, versions, propsGenerator);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(IReadOnlyCollection<PackageUpdateEntry> subset)
    {
        // Always start from the pristine file: MergeExisting reads whatever is on disk, so layering a
        // second apply on a mutated file would leave versions from the previous subset behind.
        await RevertAsync();

        if (subset.Count == 0)
        {
            return;
        }

        var target = new Dictionary<string, HashSet<string>>(_baselineVersions, StringComparer.OrdinalIgnoreCase);
        foreach (var update in subset)
        {
            target[update.PackageName] = [update.LatestVersion];
        }

        var (content, _, _, _) = _propsGenerator.MergeExisting(_propsPath, target);
        await FileHelper.WriteAtomicAsync(_propsPath, content);
    }

    /// <inheritdoc />
    public Task RevertAsync() => FileHelper.WriteAtomicAsync(_propsPath, _baselineContent);
}
