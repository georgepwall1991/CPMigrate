namespace CPMigrate.Services;

/// <summary>
/// Provides atomic file write operations to prevent corruption from interrupted writes.
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// Writes content to a file atomically by writing to a temp file first, then moving it.
    /// This prevents file corruption if the process is interrupted mid-write.
    /// </summary>
    public static async Task WriteAtomicAsync(string path, string content)
    {
        var directory = PrepareTarget(path);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            CleanupTemp(tempPath);
        }
    }

    /// <summary>
    /// The synchronous form of <see cref="WriteAtomicAsync"/> for call sites — fixer output,
    /// reports, config files — that have no async plumbing. Same guarantee: the target is only
    /// ever replaced by a fully-written file.
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var directory = PrepareTarget(path);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            CleanupTemp(tempPath);
        }
    }

    private static string PrepareTarget(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        // The target can live in a subtree that does not exist yet — a declared
        // DirectoryPackagesPropsPath may point into one — so the parent is created rather
        // than assumed. No-op when it is already there.
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CleanupTemp(string tempPath)
    {
        // Clean up temp file if move failed
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
