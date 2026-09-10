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
        // The same resolution --prune-backups and --rollback use: --backup-dir names the
        // container, and the sets live in its .cpmigrate_backup child. Scanning BackupDir
        // directly meant the default invocation never found the backups a migration just made.
        var backupPath = BackupManager.GetBackupDirectoryPath(options);

        // Under --output Json the stdout contract is one parseable document, so the banner, the
        // missing-directory warning, and the table must not leak into it. The document carries
        // the missing-directory verdict as `directoryExists` — the same answer the console gives
        // as a warning, as data a consumer can read.
        if (options.Output == OutputFormat.Json)
        {
            var directoryExists = Directory.Exists(backupPath);
            var backups = directoryExists
                ? _backupManager.GetBackupHistory(backupPath)
                : new List<BackupSetInfo>();

            // EmitAsync, not EmitFailureAsync: a document that could not reach its --output-file
            // must not fall back to stdout and exit 0 — a CI job expecting the file would pass
            // with a missing artifact.
            await JsonOutputWriter.EmitAsync(
                ListBackupsJsonWriter.Serialize(
                    backupPath,
                    directoryExists,
                    backups,
                    ExitCodes.Success
                ),
                options,
                _consoleService
            );
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        if (!_quietMode)
        {
            _consoleService.Banner("BACKUP HISTORY");
            _consoleService.WriteLine();
        }

        if (!Directory.Exists(backupPath))
        {
            _consoleService.Warning($"Backup directory not found: {backupPath}");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        var history = _backupManager.GetBackupHistory(backupPath);

        if (history.Count == 0)
        {
            _consoleService.Info("No backups found.");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(SpectrePalette.CyberColors.Dim)
            .Title($"[{SpectrePalette.Ink.Muted}] BACKUP HISTORY [/]")
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]#[/]").Centered())
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]Date/Time[/]"))
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]Age[/]"))
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]Files[/]").RightAligned())
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]Size[/]").RightAligned());

        var (totalSize, totalFiles) = PopulateBackupTable(table, history);

        AnsiConsole.Write(table);
        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }

        _consoleService.Info($"Total: {history.Count} backup set(s), {totalFiles} file(s), {FormatFileSize(totalSize)}");
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
        var age = FormatRelativeAge(backup.ParsedTimestamp);

        table.AddRow(
            $"[{SpectrePalette.Ink.Secondary}]{index}[/]",
            $"[{SpectrePalette.Ink.Text}]{displayTime}[/]",
            $"[{SpectrePalette.Ink.Dim}]{age}[/]",
            $"[{SpectrePalette.Ink.Accent}]{backup.Files.Count}[/]",
            $"[{SpectrePalette.Ink.Success}]{FormatFileSize(backupSize)}[/]"
        );
    }

    private static string FormatRelativeAge(DateTime? timestamp)
    {
        if (timestamp is null)
        {
            return "unknown";
        }

        var span = DateTime.UtcNow - timestamp.Value;

        if (span.TotalMinutes < 1)
        {
            return "just now";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes}m ago";
        }

        if (span.TotalHours < 24)
        {
            return $"{(int)span.TotalHours}h ago";
        }

        return $"{(int)span.TotalDays}d ago";
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
