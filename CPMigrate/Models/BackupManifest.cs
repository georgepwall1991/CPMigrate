namespace CPMigrate.Models;

/// <summary>
/// Manifest file tracking backup information for rollback support.
/// Stored as backup_manifest.json in the backup directory.
/// </summary>
public class BackupManifest
{
    /// <summary>
    /// Timestamp when the migration was performed (format: yyyyMMddHHmmssfff).
    /// </summary>
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the Directory.Packages.props file that was created.
    /// </summary>
    public string PropsFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether Directory.Packages.props existed before migration.
    /// </summary>
    public bool PropsFileExisted { get; set; }

    /// <summary>
    /// List of backup entries mapping original files to their backups.
    /// </summary>
    public List<BackupEntry> Backups { get; set; } = new();
}
