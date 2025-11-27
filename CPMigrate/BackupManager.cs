namespace CPMigrate;

/// <summary>
/// Manages backup operations for project files during CPM migration.
/// </summary>
public class BackupManager
{
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
}
