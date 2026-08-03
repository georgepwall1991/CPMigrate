using System.Text.Json;
using CPMigrate.Models;

namespace CPMigrate;

// BackupSetInfo and PruneResult are in Models/BackupModels.cs

/// <summary>
/// Manages backup operations for project files during CPM migration.
/// </summary>
public class BackupManager : IBackupManager
{
    private const string ManifestFileName = "backup_manifest.json";
    private const string BackupDirectoryName = ".cpmigrate_backup";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Creates the backup directory if backups are enabled.
    /// </summary>
    /// <param name="options">Migration options containing backup settings.</param>
    /// <returns>The full path to the created backup directory, or empty string if backups are disabled.</returns>
    /// <exception cref="IOException">Thrown when the backup directory cannot be created.</exception>
    public static string CreateBackupDirectory(Options options)
    {
        return CreateBackupDirectory(BackupSettings.FromOptions(options));
    }

    public static string CreateBackupDirectory(BackupSettings backupSettings)
    {
        if (!backupSettings.Enabled)
        {
            return string.Empty;
        }

        var backupPath = Path.Combine(
            Path.GetFullPath(
                string.IsNullOrWhiteSpace(backupSettings.BackupDir) ? "." : backupSettings.BackupDir
            ),
            BackupDirectoryName
        );

        try
        {
            if (!Directory.Exists(backupPath))
            {
                Directory.CreateDirectory(backupPath);
            }

            return backupPath;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException(
                $"Cannot create backup directory '{backupPath}': {ex.Message}",
                ex
            );
        }
    }

    /// <summary>
    /// Creates a timestamped backup of a file and returns the backup entry.
    /// </summary>
    /// <param name="options">Migration options containing backup settings.</param>
    /// <param name="filePath">Full path to the file to backup.</param>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="timestampOverride">Optional timestamp to group backups into a single set.</param>
    /// <returns>The backup entry, or null if backups are disabled.</returns>
    /// <exception cref="IOException">Thrown when the backup file cannot be created.</exception>
    public BackupEntry? CreateBackupForProject(
        Options options,
        string filePath,
        string backupPath,
        string? timestampOverride = null
    )
    {
        return CreateBackupForProject(
            BackupSettings.FromOptions(options),
            filePath,
            backupPath,
            timestampOverride
        );
    }

    public BackupEntry? CreateBackupForProject(
        BackupSettings backupSettings,
        string filePath,
        string backupPath,
        string? timestampOverride = null
    )
    {
        if (!backupSettings.Enabled)
        {
            return null;
        }

        var fileName = Path.GetFileName(filePath);
        // Use milliseconds for timestamp precision to avoid collisions in fast/parallel operations
        var timestamp = timestampOverride ?? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupFileName = ResolveBackupFileName(filePath, fileName, timestamp, backupPath);
        var backupFilePath = Path.Combine(backupPath, backupFileName);

        try
        {
            File.Copy(filePath, backupFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException($"Failed to create backup for '{filePath}': {ex.Message}", ex);
        }

        return new BackupEntry
        {
            OriginalPath = Path.GetFullPath(filePath),
            BackupFileName = backupFileName,
        };
    }

    /// <summary>
    /// A backup file name that cannot collide with another project's in the same set.
    /// </summary>
    /// <remarks>
    /// Backups were named from the file name and one timestamp shared by the whole run, so a solution
    /// holding <c>src/Api/Api.csproj</c> and <c>tests/Api/Api.csproj</c> produced the same name twice.
    /// The copy overwrote, the manifest kept both entries pointing at the one surviving file, and a
    /// rollback wrote the second project's bytes into both originals — reporting success. Silent
    /// corruption by the mechanism the tool offers as its undo.
    ///
    /// <para>Pre-existing, and found by cross-review of the resolved-graph verification, which made it
    /// matter more: verification rolls back automatically and unattended, so nobody is watching.</para>
    ///
    /// <para>The disambiguator goes inside the file-name part rather than after the timestamp, because
    /// <see cref="GetBackupHistory"/> groups a backup set by everything following <c>.backup_</c>. Appending
    /// there would give every project its own "set". The ordinary case keeps its plain name: the hash
    /// is added only when the plain one is already taken, so existing manifests and listings are
    /// unaffected.</para>
    /// </remarks>
    private static string ResolveBackupFileName(
        string filePath,
        string fileName,
        string timestamp,
        string backupPath
    )
    {
        var plain = $"{fileName}.backup_{timestamp}";

        if (!File.Exists(Path.Combine(backupPath, plain)))
        {
            return plain;
        }

        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(filePath))
        );

        return $"{fileName}__{Convert.ToHexString(digest)[..8].ToLowerInvariant()}.backup_{timestamp}";
    }

    /// <summary>
    /// Manages the .gitignore file to optionally include the backup directory.
    /// </summary>
    /// <param name="options">Migration options containing gitignore settings.</param>
    /// <param name="backupPath">Path to the backup directory to add to gitignore.</param>
    public static Task ManageGitIgnore(Options options, string? backupPath)
    {
        return ManageGitIgnore(BackupSettings.FromOptions(options), backupPath);
    }

    public static async Task ManageGitIgnore(BackupSettings backupSettings, string? backupPath)
    {
        if (
            !backupSettings.AddBackupToGitignore
            || !backupSettings.Enabled
            || string.IsNullOrEmpty(backupPath)
        )
        {
            return;
        }

        var gitignorePath = Path.Combine(
            Path.GetFullPath(
                string.IsNullOrEmpty(backupSettings.GitignoreDir)
                    ? "."
                    : backupSettings.GitignoreDir
            ),
            ".gitignore"
        );

        var backupDirName = Path.GetFileName(backupPath);
        var entryToAdd = $"{backupDirName}/";

        if (File.Exists(gitignorePath))
        {
            var lines = await File.ReadAllLinesAsync(gitignorePath);
            // Check for exact line match (with or without trailing slash)
            var alreadyExists = lines.Any(line =>
            {
                var trimmed = line.Trim();
                return trimmed == backupDirName || trimmed == entryToAdd;
            });

            if (alreadyExists)
            {
                return; // Already in gitignore
            }

            await File.AppendAllTextAsync(
                gitignorePath,
                $"{Environment.NewLine}# CPMigrate backup directory{Environment.NewLine}{entryToAdd}{Environment.NewLine}"
            );
        }
        else
        {
            await File.WriteAllTextAsync(
                gitignorePath,
                $"# CPMigrate backup directory{Environment.NewLine}{entryToAdd}{Environment.NewLine}"
            );
        }
    }

    /// <summary>
    /// Writes the backup manifest to track files for rollback.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="manifest">The manifest to write.</param>
    public static async Task WriteManifestAsync(string backupPath, BackupManifest manifest)
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
    public static async Task<BackupManifest?> ReadManifestAsync(string backupPath)
    {
        var manifestPath = Path.Combine(backupPath, ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath);
            return JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Log the error details for debugging - manifest is likely corrupted
            await Console.Error.WriteLineAsync(
                $"Warning: Failed to parse backup manifest: {ex.Message}"
            );
            return null;
        }
    }

    /// <summary>
    /// Gets the full path to the backup directory.
    /// </summary>
    /// <param name="options">Migration options containing backup settings.</param>
    /// <returns>The full path to the backup directory.</returns>
    public static string GetBackupDirectoryPath(Options options)
    {
        return GetBackupDirectoryPath(BackupSettings.FromOptions(options));
    }

    public static string GetBackupDirectoryPath(BackupSettings backupSettings)
    {
        return Path.Combine(
            Path.GetFullPath(
                string.IsNullOrWhiteSpace(backupSettings.BackupDir) ? "." : backupSettings.BackupDir
            ),
            BackupDirectoryName
        );
    }

    /// <summary>
    /// Restores a single file from backup to its original location.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="entry">The backup entry containing paths.</param>
    public static void RestoreFile(string backupPath, BackupEntry entry)
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
    /// <returns>List of any errors encountered during cleanup (cleanup continues on errors).</returns>
    public static List<string> CleanupBackups(string backupPath, BackupManifest manifest)
    {
        var errors = new List<string>();

        // Delete backup files
        var deleteErrors = manifest
            .Backups.Select(entry => TryDeleteBackupFile(backupPath, entry))
            .Where(error => error != null)
            .Cast<string>();
        errors.AddRange(deleteErrors);

        // Delete manifest
        var manifestPath = Path.Combine(backupPath, ManifestFileName);
        try
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            errors.Add($"Failed to delete manifest: {ex.Message}");
        }

        // Delete backup directory if empty
        try
        {
            if (
                Directory.Exists(backupPath)
                && !Directory.EnumerateFileSystemEntries(backupPath).Any()
            )
            {
                Directory.Delete(backupPath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            errors.Add($"Failed to delete backup directory: {ex.Message}");
        }

        return errors;
    }

    private static string? TryDeleteBackupFile(string backupPath, BackupEntry entry)
    {
        var backupFilePath = Path.Combine(backupPath, entry.BackupFileName);
        try
        {
            if (File.Exists(backupFilePath))
            {
                File.Delete(backupFilePath);
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"Failed to delete backup file '{entry.BackupFileName}': {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 - Backup Pruning and Retention
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gets a list of all backup sets in the backup directory, sorted by timestamp (newest first).
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <returns>List of backup info with timestamps and file counts.</returns>
    public List<BackupSetInfo> GetBackupHistory(string backupPath)
    {
        var backups = new Dictionary<string, BackupSetInfo>();

        if (!Directory.Exists(backupPath))
        {
            return new List<BackupSetInfo>();
        }

        // Find all backup files and group by timestamp
        foreach (var file in Directory.GetFiles(backupPath, "*.backup_*"))
        {
            var fileName = Path.GetFileName(file);
            var timestampStart = fileName.LastIndexOf(".backup_", StringComparison.Ordinal);
            if (timestampStart < 0)
            {
                continue;
            }

            var timestamp = fileName[(timestampStart + 8)..]; // Skip ".backup_"

            if (!backups.TryGetValue(timestamp, out var info))
            {
                info = new BackupSetInfo { Timestamp = timestamp, Files = new List<string>() };
                backups[timestamp] = info;
            }

            info.Files.Add(file);
        }

        // Sort by timestamp descending (newest first)
        return backups.Values.OrderByDescending(b => b.Timestamp).ToList();
    }

    /// <summary>
    /// Prunes old backups, keeping only the specified number of most recent.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="keep">Number of backup sets to keep.</param>
    /// <returns>Prune result with counts and freed space.</returns>
    public PruneResult PruneBackups(string backupPath, int keep)
    {
        var result = new PruneResult();
        var backups = GetBackupHistory(backupPath);

        if (keep <= 0 || backups.Count <= keep)
        {
            result.KeptCount = backups.Count;
            return result;
        }

        var toKeep = backups.Take(keep).ToList();
        var toRemove = backups.Skip(keep).ToList();

        result.KeptCount = toKeep.Count;

        foreach (var backup in toRemove)
        {
            foreach (var file in backup.Files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    result.BytesFreed += fileInfo.Length;
                    File.Delete(file);
                    result.FilesRemoved++;
                }
                catch (Exception ex)
                {
                    // Track but continue
                    result.Errors.Add($"Failed to delete: {file} ({ex.Message})");
                }
            }
            result.BackupsRemoved++;
        }

        return result;
    }

    /// <summary>
    /// Deletes ALL backups in the backup directory.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <returns>Prune result with counts and freed space.</returns>
    public PruneResult PruneAllBackups(string backupPath)
    {
        var result = new PruneResult();

        if (!Directory.Exists(backupPath))
        {
            return result;
        }

        var backups = GetBackupHistory(backupPath);
        DeleteBackupSets(backups, result);
        DeleteManifestFile(backupPath, result);
        TryDeleteEmptyDirectory(backupPath);

        return result;
    }

    /// <summary>
    /// Deletes all files in the specified backup sets.
    /// </summary>
    private static void DeleteBackupSets(List<BackupSetInfo> backups, PruneResult result)
    {
        foreach (var backup in backups)
        {
            DeleteBackupFiles(backup.Files, result);
            result.BackupsRemoved++;
        }
    }

    /// <summary>
    /// Deletes a list of backup files, tracking results.
    /// </summary>
    private static void DeleteBackupFiles(List<string> files, PruneResult result)
    {
        foreach (var file in files)
        {
            TryDeleteFile(file, result);
        }
    }

    /// <summary>
    /// Attempts to delete a single file, tracking result.
    /// </summary>
    private static void TryDeleteFile(string filePath, PruneResult result)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            result.BytesFreed += fileInfo.Length;
            File.Delete(filePath);
            result.FilesRemoved++;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to delete: {filePath} ({ex.Message})");
        }
    }

    /// <summary>
    /// Deletes the manifest file if it exists.
    /// </summary>
    private static void DeleteManifestFile(string backupPath, PruneResult result)
    {
        var manifestPath = Path.Combine(backupPath, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            TryDeleteFile(manifestPath, result);
        }
    }

    /// <summary>
    /// Attempts to delete an empty directory.
    /// </summary>
    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception)
        {
            // Ignore - directory not empty or access denied
        }
    }

    /// <summary>
    /// Applies automatic retention after a successful migration.
    /// </summary>
    /// <param name="backupPath">Path to the backup directory.</param>
    /// <param name="maxBackups">Maximum number of backups to retain.</param>
    /// <returns>Prune result if any backups were removed.</returns>
    public PruneResult ApplyRetention(string backupPath, int maxBackups)
    {
        if (maxBackups <= 0)
        {
            return new PruneResult(); // Retention disabled
        }

        return PruneBackups(backupPath, maxBackups);
    }
}
