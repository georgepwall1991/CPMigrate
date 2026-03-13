using CPMigrate.Models;

namespace CPMigrate;

/// <summary>
/// Interface for backup management operations.
/// </summary>
public interface IBackupManager
{
    /// <summary>
    /// Creates a timestamped backup of a file and returns the backup entry.
    /// </summary>
    BackupEntry? CreateBackupForProject(BackupSettings backupSettings, string filePath, string backupPath, string? timestampOverride = null);

    /// <summary>
    /// Creates a timestamped backup of a file and returns the backup entry.
    /// </summary>
    BackupEntry? CreateBackupForProject(Options options, string filePath, string backupPath, string? timestampOverride = null);

    /// <summary>
    /// Gets a list of all backup sets in the backup directory, sorted by timestamp (newest first).
    /// </summary>
    List<BackupSetInfo> GetBackupHistory(string backupPath);

    /// <summary>
    /// Prunes old backups, keeping only the specified number of most recent.
    /// </summary>
    PruneResult PruneBackups(string backupPath, int keep);

    /// <summary>
    /// Deletes ALL backups in the backup directory.
    /// </summary>
    PruneResult PruneAllBackups(string backupPath);

    /// <summary>
    /// Applies automatic retention after a successful migration.
    /// </summary>
    PruneResult ApplyRetention(string backupPath, int maxBackups);
}
