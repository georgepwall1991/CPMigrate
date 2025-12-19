namespace CPMigrate.Models;

/// <summary>
/// Information about a backup set (all files from a single backup operation).
/// </summary>
public class BackupSetInfo
{
    /// <summary>
    /// Timestamp of the backup (format: yyyyMMddHHmmssZ).
    /// </summary>
    public string Timestamp { get; init; } = string.Empty;

    /// <summary>
    /// List of backup file paths in this set.
    /// </summary>
    public List<string> Files { get; init; } = new();

    /// <summary>
    /// Number of files in this backup set.
    /// </summary>
    public int FileCount => Files.Count;

    /// <summary>
    /// Total size of all files in this backup set.
    /// </summary>
    public long TotalSize => Files.Sum(f => new FileInfo(f).Length);

    /// <summary>
    /// Parsed DateTime from the timestamp.
    /// </summary>
    public DateTime? ParsedTimestamp
    {
        get
        {
            // Try new format with milliseconds first, then legacy format
            string[] formats = { "yyyyMMddHHmmssfff", "yyyyMMddHHmmssZ" };
            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(Timestamp, format,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                {
                    return dt;
                }
            }
            return null;
        }
    }
}

/// <summary>
/// Result of a prune operation.
/// </summary>
public class PruneResult
{
    /// <summary>
    /// Number of backup sets kept.
    /// </summary>
    public int KeptCount { get; set; }

    /// <summary>
    /// Number of backup sets removed.
    /// </summary>
    public int BackupsRemoved { get; set; }

    /// <summary>
    /// Number of files removed.
    /// </summary>
    public int FilesRemoved { get; set; }

    /// <summary>
    /// Total bytes freed by the prune operation.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Errors encountered during pruning.
    /// </summary>
    public List<string> Errors { get; } = new();

    /// <summary>
    /// Whether the prune operation was successful (no errors).
    /// </summary>
    public bool Success => Errors.Count == 0;

    /// <summary>
    /// Human-readable string of bytes freed.
    /// </summary>
    public string BytesFreedFormatted => FormatBytes(BytesFreed);

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F2} GB",
            >= MB => $"{bytes / (double)MB:F2} MB",
            >= KB => $"{bytes / (double)KB:F2} KB",
            _ => $"{bytes} bytes"
        };
    }
}
