using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services.Migration;

internal sealed class BackupCoordinator
{
    private readonly IBackupManager _backupManager;
    private readonly IConsoleService _consoleService;
    private readonly bool _quietMode;

    public BackupCoordinator(IBackupManager backupManager, IConsoleService consoleService, bool quietMode)
    {
        _backupManager = backupManager;
        _consoleService = consoleService;
        _quietMode = quietMode;
    }

    public string? SetupBackupDirectory(MigrationRequest request)
    {
        if (request.DryRun)
        {
            if (request.Backup.Enabled && !_quietMode)
            {
                var potentialBackupPath = Path.Combine(
                    Path.GetFullPath(string.IsNullOrEmpty(request.Backup.BackupDir) ? "." : request.Backup.BackupDir),
                    ".cpmigrate_backup");
                _consoleService.DryRun($"Would create backup directory: {potentialBackupPath}");
            }

            if (!_quietMode)
            {
                _consoleService.WriteLine();
            }

            return null;
        }

        var backupPath = BackupManager.CreateBackupDirectory(request.Backup);
        if (!string.IsNullOrEmpty(backupPath) && !_quietMode)
        {
            _consoleService.WriteMarkup($"[dim]:file_folder: Backup directory: {Markup.Escape(backupPath)}[/]\n");
        }

        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }

        return backupPath;
    }

    public async Task WriteManifestAsync(
        MigrationRequest request,
        List<BackupEntry> backupEntries,
        string? backupPath,
        string propsFilePath,
        bool propsFileExisted,
        string? backupTimestamp)
    {
        if (request.DryRun || !request.Backup.Enabled || backupEntries.Count == 0 || string.IsNullOrEmpty(backupPath))
        {
            return;
        }

        var manifestTimestamp = backupTimestamp ?? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var manifest = new BackupManifest
        {
            Timestamp = manifestTimestamp,
            PropsFilePath = propsFilePath,
            PropsFileExisted = propsFileExisted,
            Backups = backupEntries
        };
        await BackupManager.WriteManifestAsync(backupPath, manifest);
    }

    public BackupEntry? CreatePropsBackup(
        MigrationRequest request,
        bool propsFileExists,
        string propsPath,
        string? backupPath,
        string? backupTimestamp)
    {
        if (!propsFileExists || !request.MergeExisting || request.DryRun || !request.Backup.Enabled || string.IsNullOrEmpty(backupPath))
        {
            return null;
        }

        var propsBackupEntry = _backupManager.CreateBackupForProject(request.Backup, propsPath, backupPath, backupTimestamp);
        if (propsBackupEntry != null && !_quietMode)
        {
            _consoleService.Dim("Backed up existing Directory.Packages.props.");
        }

        return propsBackupEntry;
    }

    public async Task ManageGitIgnoreAsync(MigrationRequest request, string? backupPath)
    {
        if (!request.DryRun)
        {
            await BackupManager.ManageGitIgnore(request.Backup, backupPath);
        }
        else if (request.Backup.AddBackupToGitignore && request.Backup.Enabled && !_quietMode)
        {
            _consoleService.DryRun("Would add backup directory to .gitignore");
        }
    }

    public static RollbackRequest CreateRollbackRequest(CommandOutput output, string backupPath, string fallbackBackupDir)
    {
        return new RollbackRequest(
            Backup: new BackupSettings(
                Enabled: true,
                BackupDir: ResolveRollbackBackupDir(backupPath, fallbackBackupDir),
                AddBackupToGitignore: false,
                GitignoreDir: fallbackBackupDir),
            Output: output);
    }

    private static string ResolveRollbackBackupDir(string backupPath, string fallbackBackupDir)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return fallbackBackupDir;
        }

        var normalizedPath = backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(normalizedPath), ".cpmigrate_backup", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(normalizedPath) ?? fallbackBackupDir;
        }

        return normalizedPath;
    }
}
