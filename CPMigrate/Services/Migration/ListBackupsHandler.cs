using System.Globalization;
using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services.Migration;

internal sealed class ListBackupsHandler
{
    private readonly IBackupManager _backupManager;
    private readonly IConsoleService _consoleService;
    private readonly bool _quietMode;

    public ListBackupsHandler(IBackupManager backupManager, IConsoleService consoleService, bool quietMode)
    {
        _backupManager = backupManager;
        _consoleService = consoleService;
        _quietMode = quietMode;
    }

    public async Task<MigrationResult> ExecuteAsync(Options options)
    {
        if (!_quietMode)
        {
            _consoleService.Banner("BACKUP HISTORY");
            _consoleService.WriteLine();
        }

        var backupPath = Path.GetFullPath(options.BackupDir);

        if (!Directory.Exists(backupPath))
        {
            _consoleService.Warning($"Backup directory not found: {backupPath}");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        var backups = _backupManager.GetBackupHistory(backupPath);

        if (backups.Count == 0)
        {
            _consoleService.Info("No backups found.");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[cyan]#[/]").Centered())
            .AddColumn(new TableColumn("[cyan]Timestamp[/]"))
            .AddColumn(new TableColumn("[cyan]Date/Time[/]"))
            .AddColumn(new TableColumn("[cyan]Files[/]").RightAligned())
            .AddColumn(new TableColumn("[cyan]Size[/]").RightAligned());

        var (totalSize, totalFiles) = PopulateBackupTable(table, backups);

        AnsiConsole.Write(table);
        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }

        _consoleService.Info($"Total: {backups.Count} backup set(s), {totalFiles} file(s), {FormatFileSize(totalSize)}");
        _consoleService.Dim($"Backup directory: {backupPath}");

        return await Task.FromResult(new MigrationResult { ExitCode = ExitCodes.Success });
    }

    private static (long TotalSize, int TotalFiles) PopulateBackupTable(Table table, List<BackupSetInfo> backups)
    {
        var index = 1;
        long totalSize = 0;
        int totalFiles = 0;

        foreach (var backup in backups)
        {
            var displayTime = ParseBackupTimestamp(backup.Timestamp);
            var backupSize = CalculateBackupSize(backup.Files);
            totalSize += backupSize;
            totalFiles += backup.Files.Count;

            AddBackupTableRow(table, index, backup, displayTime, backupSize);
            index++;
        }

        return (totalSize, totalFiles);
    }

    private static string ParseBackupTimestamp(string timestamp)
    {
        if (DateTime.TryParseExact(timestamp, "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return timestamp;
    }

    private static long CalculateBackupSize(List<string> files)
    {
        return files.Sum(f =>
        {
            try
            {
                return File.Exists(f) ? new FileInfo(f).Length : 0;
            }
            catch
            {
                return 0;
            }
        });
    }

    private static void AddBackupTableRow(Table table, int index, BackupSetInfo backup,
        string displayTime, long backupSize)
    {
        table.AddRow(
            $"[cyan]{index}[/]",
            $"[dim]{backup.Timestamp}[/]",
            $"[white]{displayTime}[/]",
            $"[yellow]{backup.Files.Count}[/]",
            $"[green]{FormatFileSize(backupSize)}[/]"
        );
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
