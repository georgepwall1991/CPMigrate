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

    /// <summary>
    /// SHA-256 of the original file's bytes, recorded when the backup was created. Null for
    /// entries in manifests written before integrity verification existed; such entries are
    /// restored without a check.
    /// </summary>
    public string? Sha256 { get; set; }
}
