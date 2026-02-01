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
        var directory = Path.GetDirectoryName(path) ?? ".";
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
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
}
