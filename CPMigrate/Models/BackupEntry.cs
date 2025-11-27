namespace CPMigrate.Models;

/// <summary>
/// Represents a single backup entry mapping an original file to its backup.
/// </summary>
public class BackupEntry
{
    /// <summary>
    /// Full path to the original .csproj file.
    /// </summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>
    /// Filename of the backup file (without directory path).
    /// </summary>
    public string BackupFileName { get; set; } = string.Empty;
}
