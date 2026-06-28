using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services.Migration;

internal sealed class RollbackHandler
{
    private readonly IConsoleService _consoleService;
    private readonly bool _quietMode;

    public RollbackHandler(IConsoleService consoleService, bool quietMode)
    {
        _consoleService = consoleService;
        _quietMode = quietMode;
    }

    public async Task<MigrationResult> ExecuteAsync(Options options)
    {
        if (!_quietMode)
        {
            _consoleService.Banner("ROLLBACK MODE - Restoring from backup");
            _consoleService.WriteLine();
        }

        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        var validationError = ValidateRollbackPrerequisites(backupPath);
        if (validationError != null)
        {
            return validationError;
        }

        var manifest = await BackupManager.ReadManifestAsync(backupPath);
        if (manifest == null || manifest.Backups.Count == 0)
        {
            return HandleEmptyOrMissingManifest(manifest);
        }

        if (!ShowPreviewAndConfirm(options, manifest))
        {
            _consoleService.Info("Rollback cancelled.");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        _consoleService.WriteLine();

        var (restoredCount, failedCount) = await RestoreFilesWithProgress(backupPath, manifest);
        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }

        HandlePostRestoreCleanup(backupPath, manifest, failedCount);
        ShowRollbackSummary(restoredCount, failedCount);

        return new MigrationResult
        {
            ProjectsProcessed = restoredCount,
            ExitCode = failedCount == 0 ? ExitCodes.Success : ExitCodes.FileOperationError
        };
    }

    private MigrationResult? ValidateRollbackPrerequisites(string backupPath)
    {
        if (!Directory.Exists(backupPath))
        {
            _consoleService.Error($"No backup directory found at: {backupPath}");
            _consoleService.WriteMarkup("[dim]Run a migration first to create backups.[/]\n");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }

        return null;
    }

    private MigrationResult HandleEmptyOrMissingManifest(BackupManifest? manifest)
    {
        if (manifest == null)
        {
            _consoleService.Error("No backup manifest found or manifest is corrupted.");
            _consoleService.WriteMarkup("[dim]Cannot determine which files to restore.[/]\n");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }

        _consoleService.Warning("No backup entries found in manifest - nothing to restore.");
        _consoleService.Dim("The backup manifest exists but contains no files. This may indicate:");
        _consoleService.Dim("  - A previous rollback already completed");
        _consoleService.Dim("  - The migration was run with --no-backup");
        return new MigrationResult { ExitCode = ExitCodes.Success };
    }

    private bool ShowPreviewAndConfirm(Options options, BackupManifest manifest)
    {
        if (options.Force)
        {
            return true;
        }

        if (_quietMode)
        {
            if (options.Output == OutputFormat.Json)
            {
                return false;
            }

            return true;
        }

        var filesToRestore = manifest.Backups.Select(b => b.OriginalPath).ToList();
        _consoleService.WriteRollbackPreview(filesToRestore, manifest.PropsFilePath);
        return _consoleService.AskConfirmation("Proceed with rollback?");
    }

    private async Task<(int RestoredCount, int FailedCount)> RestoreFilesWithProgress(
        string backupPath,
        BackupManifest manifest)
    {
        var restoredCount = 0;
        var failedCount = 0;

        if (_quietMode)
        {
            foreach (var entry in manifest.Backups)
            {
                var fileName = Path.GetFileName(entry.OriginalPath);
                if (TryRestoreFile(backupPath, entry, fileName))
                {
                    restoredCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            return (restoredCount, failedCount);
        }

        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Restoring files[/]", maxValue: manifest.Backups.Count);

                foreach (var entry in manifest.Backups)
                {
                    var fileName = Path.GetFileName(entry.OriginalPath);
                    task.Description = $"[cyan]Restoring[/] [white]{Markup.Escape(fileName)}[/]";

                    if (TryRestoreFile(backupPath, entry, fileName))
                    {
                        restoredCount++;
                    }
                    else
                    {
                        failedCount++;
                    }

                    task.Increment(1);
                    await Task.Delay(50);
                }

                task.Description = "[green]Restore complete[/]";
            });

        return (restoredCount, failedCount);
    }

    private bool TryRestoreFile(string backupPath, BackupEntry entry, string fileName)
    {
        try
        {
            BackupManager.RestoreFile(backupPath, entry);
            return true;
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Failed to restore {fileName}: {ex.Message}");
            return false;
        }
    }

    private void HandlePostRestoreCleanup(string backupPath, BackupManifest manifest, int failedCount)
    {
        if (failedCount == 0)
        {
            HandleSuccessfulRestore(backupPath, manifest);
        }
        else
        {
            HandleFailedRestore(manifest);
        }
    }

    private void HandleSuccessfulRestore(string backupPath, BackupManifest manifest)
    {
        DeletePropsFileIfNeeded(manifest.PropsFilePath, manifest.PropsFileExisted);
        CleanupBackupFiles(backupPath, manifest);
    }

    private void DeletePropsFileIfNeeded(string propsFilePath, bool propsFileExisted)
    {
        if (string.IsNullOrEmpty(propsFilePath))
        {
            return;
        }

        if (!propsFileExisted && File.Exists(propsFilePath))
        {
            TryDeletePropsFile(propsFilePath);
        }
        else if (propsFileExisted)
        {
            _consoleService.Dim("Preserved existing Directory.Packages.props.");
        }
    }

    private void TryDeletePropsFile(string propsFilePath)
    {
        try
        {
            File.Delete(propsFilePath);
            _consoleService.Success($"Deleted: {propsFilePath}");
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not delete props file: {ex.Message}");
        }
    }

    private void CleanupBackupFiles(string backupPath, BackupManifest manifest)
    {
        if (string.IsNullOrEmpty(backupPath))
        {
            return;
        }

        var cleanupErrors = BackupManager.CleanupBackups(backupPath, manifest);

        if (cleanupErrors.Count == 0)
        {
            _consoleService.Dim("Cleaned up backup files.");
        }
        else
        {
            ShowCleanupErrors(cleanupErrors);
        }
    }

    private void ShowCleanupErrors(List<string> cleanupErrors)
    {
        _consoleService.Warning($"Cleanup completed with {cleanupErrors.Count} error(s):");
        foreach (var error in cleanupErrors)
        {
            _consoleService.Dim($"  - {error}");
        }
    }

    private void HandleFailedRestore(BackupManifest manifest)
    {
        _consoleService.Warning(manifest.PropsFileExisted
            ? "Existing props file retained due to restore failures."
            : "Props file NOT deleted due to restore failures.");
        _consoleService.Dim("Backup files retained for manual recovery.");
    }

    private void ShowRollbackSummary(int restoredCount, int failedCount)
    {
        if (_quietMode)
        {
            return;
        }

        _consoleService.WriteLine();

        if (failedCount == 0)
        {
            _consoleService.Success($"Rollback complete! Restored {restoredCount} file(s).");
        }
        else
        {
            _consoleService.Warning($"Rollback completed with errors. Restored: {restoredCount}, Failed: {failedCount}");
            _consoleService.Dim("Manual intervention may be required. Check backup directory for original files.");
        }
    }
}
