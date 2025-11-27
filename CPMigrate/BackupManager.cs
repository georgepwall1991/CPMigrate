using System.Text.Json;
using CPMigrate.Models;

namespace CPMigrate;

/// <summary>
/// Manages backup operations for project files during CPM migration.
/// </summary>
public class BackupManager
{
    private const string ManifestFileName = "backup_manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    /// <summary>
    /// Creates the backup directory if backups are enabled.
    /// </summary>
    /// <param name="options">Migration options containing backup settings.</param>
    /// <returns>The full path to the created backup directory, or empty string if backups are disabled.</returns>
    public string CreateBackupDirectory(Options options)
    {
        if (options.NoBackup) return string.Empty;

        var backupPath = Path.Combine(
            Path.GetFullPath(string.IsNullOrEmpty(options.BackupDir) ? "." : options.BackupDir),
            ".cpmigrate_backup");

        if (!Directory.Exists(backupPath))
        {
            Directory.CreateDirectory(backupPath);
        }

        return backupPath;
    }

    /// <summary>
    /// Creates a timestamped backup of a project file.
    /// </summary>
    /// <param name="options">Migration options containing backup settings.</param>
    /// <param name="projectFilePath">Full path to the project file to backup.</param>
    /// <param name="backupPath">Path to the backup directory.</param>
    public void CreateBackupForProject(Options options, string projectFilePath, string backupPath)
    {
        if (options.NoBackup) return;

        var fileName = Path.GetFileName(projectFilePath);
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var backupFileName = $"{fileName}.backup_{timestamp}";
        var backupFilePath = Path.Combine(backupPath, backupFileName);

        File.Copy(projectFilePath, backupFilePath, overwrite: true);
    }

    /// <summary>
    /// Manages the .gitignore file to optionally include the backup directory.
    /// </summary>
    /// <param name="options">Migration options containing gitignore settings.</param>
    /// <param name="backupPath">Path to the backup directory to add to gitignore.</param>
    public async Task ManageGitIgnore(Options options, string? backupPath)
    {
        if (!options.AddBackupToGitignore || options.NoBackup || string.IsNullOrEmpty(backupPath))
            return;

        var gitignorePath = Path.Combine(
            Path.GetFullPath(string.IsNullOrEmpty(options.GitignoreDir) ? "." : options.GitignoreDir),
            ".gitignore");

        var backupDirName = Path.GetFileName(backupPath);
        var entryToAdd = $"{backupDirName}/";

        if (File.Exists(gitignorePath))
        {
            var content = await File.ReadAllTextAsync(gitignorePath);
            if (content.Contains(backupDirName))
                return; // Already in gitignore

            await File.AppendAllTextAsync(gitignorePath,
                $"{Environment.NewLine}# CPMigrate backup directory{Environment.NewLine}{entryToAdd}{Environment.NewLine}");
        }
        else
        {
            await File.WriteAllTextAsync(gitignorePath,
                $"# CPMigrate backup directory{Environment.NewLine}{entryToAdd}{Environment.NewLine}");
        }
    }

    /// <summary>
    /// Writes the backup manifest to track files for rollback.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="manifest">The manifest to write.</param>
    public async Task WriteManifestAsync(string backupPath, BackupManifest manifest)
    {
        var manifestPath = Path.Combine(backupPath, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(manifestPath, json);
    }

    /// <summary>
    /// Reads the backup manifest from the backup directory.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <returns>The manifest if found and valid, null otherwise.</returns>
    public async Task<BackupManifest?> ReadManifestAsync(string backupPath)
    {
        var manifestPath = Path.Combine(backupPath, ManifestFileName);

        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath);
            return JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the full path to the backup directory.
    /// </summary>
    /// <param name="options">Migration options containing backup settings.</param>
    /// <returns>The full path to the backup directory.</returns>
    public string GetBackupDirectoryPath(Options options)
    {
        return Path.Combine(
            Path.GetFullPath(string.IsNullOrEmpty(options.BackupDir) ? "." : options.BackupDir),
            ".cpmigrate_backup");
    }

    /// <summary>
    /// Restores a single file from backup to its original location.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="entry">The backup entry containing paths.</param>
    public void RestoreFile(string backupPath, BackupEntry entry)
    {
        var backupFilePath = Path.Combine(backupPath, entry.BackupFileName);

        if (!File.Exists(backupFilePath))
        {
            throw new FileNotFoundException($"Backup file not found: {entry.BackupFileName}");
        }

        File.Copy(backupFilePath, entry.OriginalPath, overwrite: true);
    }

    /// <summary>
    /// Deletes the backup files and manifest after successful restore.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="manifest">The manifest containing files to delete.</param>
    public void CleanupBackups(string backupPath, BackupManifest manifest)
    {
        // Delete backup files
        foreach (var entry in manifest.Backups)
        {
            var backupFilePath = Path.Combine(backupPath, entry.BackupFileName);
            if (File.Exists(backupFilePath))
            {
                File.Delete(backupFilePath);
            }
        }

        // Delete manifest
        var manifestPath = Path.Combine(backupPath, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        // Delete backup directory if empty
        if (Directory.Exists(backupPath) && !Directory.EnumerateFileSystemEntries(backupPath).Any())
        {
            Directory.Delete(backupPath);
        }
    }
}
