using CPMigrate.Models;
using CPMigrate.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CPMigrate;

/// <summary>
/// Routes and executes different command modes based on parsed options.
/// </summary>
internal static class CommandRouter
{
    /// <summary>
    /// Routes the command based on options and executes the appropriate mode.
    /// </summary>
    public static async Task<int> RouteCommand(
        Options options,
        IConsoleService consoleService,
        IInteractiveService interactiveService,
        VersionResolver versionResolver,
        ConfigService configService,
        IBackupManager backupManager,
        ILoggerFactory? loggerFactory = null)
    {
        // Load and merge config if available
        var startDir = DetermineStartDirectory(options);
        var config = configService.LoadConfig(startDir);
        if (config != null)
        {
            ConfigService.MergeConfig(options, config);
        }

        // Handle Update command
        if (options.Update)
        {
            return await RunUpdateModeAsync(consoleService, loggerFactory);
        }

        // Route to appropriate command handler
        if (options.Interactive)
        {
            return await RunInteractiveModeAsync(consoleService, interactiveService, versionResolver, configService, backupManager);
        }

        if (options.PruneBackups || options.PruneAll)
        {
            return await RunPruneModeAsync(options, consoleService, backupManager);
        }

        if (!string.IsNullOrEmpty(options.BatchDir))
        {
            return await RunBatchModeAsync(options, consoleService, versionResolver, backupManager, loggerFactory);
        }

        if (options.UnifyProps)
        {
            return await RunUnifyPropsModeAsync(options, consoleService, loggerFactory);
        }

        // Check for updates in background for standard commands (if not quiet)
        Task? updateCheckTask = null;
        if (!options.Quiet && !options.Output.Equals(OutputFormat.Json))
        {
            var bgLoggerFactory = loggerFactory;
            updateCheckTask = Task.Run(async () =>
            {
                using var updateService = new UpdateService(consoleService, logger: bgLoggerFactory?.CreateLogger<UpdateService>());
                var latestVersion = await updateService.CheckForUpdatesAsync();
                if (latestVersion != null)
                {
                    return latestVersion;
                }
                return null;
            });
        }

        var result = await RunMigrationAsync(options, consoleService, versionResolver, backupManager, loggerFactory);

        // Show update notification after main work completes to avoid interleaved output
        if (updateCheckTask != null)
        {
            try
            {
                var latestVersion = await (Task<NuGet.Versioning.NuGetVersion?>)updateCheckTask;
                if (latestVersion != null)
                {
                    consoleService.WriteLine();
                    consoleService.Warning($"A new version of CPMigrate is available: v{latestVersion}");
                    consoleService.Dim("Run 'cpmigrate --update' to upgrade.");
                    consoleService.WriteLine();
                }
            }
            catch (Exception)
            {
                // Update check failure should never affect the main workflow
            }
        }

        return result;
    }

    /// <summary>
    /// Executes the self-update mode.
    /// </summary>
    private static async Task<int> RunUpdateModeAsync(IConsoleService consoleService, ILoggerFactory? loggerFactory = null)
    {
        try
        {
            var updateService = new UpdateService(consoleService, logger: loggerFactory?.CreateLogger<UpdateService>());
            var success = await updateService.PerformUpdateAsync();
            return success ? ExitCodes.Success : ExitCodes.UnexpectedError;
        }
        catch (Exception ex)
        {
            consoleService.Error($"Update failed: {ex.Message}");
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Determines the starting directory from options.
    /// </summary>
    private static string DetermineStartDirectory(Options options)
    {
        if (!string.IsNullOrEmpty(options.BatchDir))
        {
            return options.BatchDir;
        }

        if (!string.IsNullOrEmpty(options.SolutionFileDir) && options.SolutionFileDir != ".")
        {
            return options.SolutionFileDir;
        }

        if (!string.IsNullOrEmpty(options.ProjectFileDir))
        {
            return options.ProjectFileDir;
        }

        return ".";
    }

    /// <summary>
    /// Executes the unify Directory.Build.props mode.
    /// </summary>
    public static async Task<int> RunUnifyPropsModeAsync(Options options, IConsoleService consoleService, ILoggerFactory? loggerFactory = null)
    {
        try
        {
            consoleService.WriteHeader();

            var projectAnalyzer = new ProjectAnalyzer(consoleService, logger: loggerFactory?.CreateLogger<ProjectAnalyzer>());
            var buildPropsService = new BuildPropsService(consoleService, projectAnalyzer);

            return await buildPropsService.UnifyPropertiesAsync(options);
        }
        catch (Exception ex)
        {
            consoleService.Error($"\nUnexpected error: {ex.Message}");
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Executes interactive mode with menu-driven workflow.
    /// </summary>
    public static async Task<int> RunInteractiveModeAsync(
        IConsoleService consoleService,
        IInteractiveService interactiveService,
        VersionResolver versionResolver,
        ConfigService configService,
        IBackupManager backupManager)
    {
        consoleService.WriteHeader();

        // Loop to allow returning to menu after operations complete
        while (true)
        {
            var options = interactiveService.RunWizard();
            if (options == null)
            {
                return ExitCodes.Success; // User cancelled or chose to exit
            }

            // Load config if available
            var startDir = DetermineStartDirectory(options);
            var config = configService.LoadConfig(startDir);
            if (config != null)
            {
                ConfigService.MergeConfig(options, config);
            }

            var result = await ExecuteInteractiveCommand(options, consoleService, versionResolver, backupManager);

            // Show result and prompt to continue
            consoleService.WriteLine();
            if (!consoleService.AskConfirmation("Return to main menu?"))
            {
                return result;
            }

            consoleService.WriteLine();
        }
    }

    /// <summary>
    /// Executes a single command from interactive mode.
    /// </summary>
    private static async Task<int> ExecuteInteractiveCommand(
        Options options,
        IConsoleService consoleService,
        VersionResolver versionResolver,
        IBackupManager backupManager)
    {
        if (!string.IsNullOrEmpty(options.BatchDir))
        {
            return await RunBatchModeAsync(options, consoleService, versionResolver, backupManager);
        }

        if (options.PruneBackups || options.PruneAll || options.ListBackups)
        {
            if (options.ListBackups)
            {
                var migrationService = new MigrationService(
                    consoleService,
                    null,
                    versionResolver,
                    null,
                    backupManager,
                    null,
                    null,
                    quietMode: options.Quiet);

                var migrationResult = await migrationService.ExecuteAsync(options);
                return migrationResult.ExitCode;
            }

            return await RunPruneModeAsync(options, consoleService, backupManager);
        }

        return await RunMigrationAsync(options, consoleService, versionResolver, backupManager);
    }

    /// <summary>
    /// Executes backup pruning mode.
    /// </summary>
    public static async Task<int> RunPruneModeAsync(
        Options options,
        IConsoleService consoleService,
        IBackupManager backupManager)
    {
        if (!ValidateOptions(options, consoleService))
        {
            return ExitCodes.ValidationError;
        }

        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        if (!Directory.Exists(backupPath))
        {
            consoleService.Error($"No backup directory found at: {backupPath}");
            return ExitCodes.FileOperationError;
        }

        consoleService.WriteHeader();

        if (options.PruneAll)
        {
            return await PruneAllBackupsAsync(options, consoleService, backupManager, backupPath);
        }

        return await PruneOldBackupsAsync(options, consoleService, backupManager, backupPath);
    }

    /// <summary>
    /// Validates options and shows error if validation fails.
    /// </summary>
    private static bool ValidateOptions(Options options, IConsoleService consoleService)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (ArgumentException ex)
        {
            consoleService.Error(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Prunes all backups from the backup directory.
    /// </summary>
    private static Task<int> PruneAllBackupsAsync(
        Options options,
        IConsoleService consoleService,
        IBackupManager backupManager,
        string backupPath)
    {
        consoleService.Banner("PRUNE ALL BACKUPS");
        consoleService.WriteLine();

        var history = backupManager.GetBackupHistory(backupPath);
        if (history.Count == 0)
        {
            consoleService.Info("No backups found to delete.");
            return Task.FromResult(ExitCodes.Success);
        }

        consoleService.Warning($"This will delete ALL {history.Count} backup set(s).");

        if (!options.Quiet && !consoleService.AskConfirmation("Are you sure you want to delete ALL backups?"))
        {
            consoleService.Info("Prune cancelled.");
            return Task.FromResult(ExitCodes.Success);
        }

        var result = backupManager.PruneAllBackups(backupPath);
        consoleService.Success($"Deleted {result.BackupsRemoved} backup set(s), {result.FilesRemoved} file(s), freed {result.BytesFreedFormatted}.");

        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
            {
                consoleService.Warning(error);
            }
        }

        return Task.FromResult(result.Success ? ExitCodes.Success : ExitCodes.FileOperationError);
    }

    /// <summary>
    /// Prunes old backups keeping the specified retention count.
    /// </summary>
    private static Task<int> PruneOldBackupsAsync(
        Options options,
        IConsoleService consoleService,
        IBackupManager backupManager,
        string backupPath)
    {
        consoleService.Banner($"PRUNE BACKUPS - Keeping last {options.Retention}");
        consoleService.WriteLine();

        var history = backupManager.GetBackupHistory(backupPath);
        if (history.Count == 0)
        {
            consoleService.Info("No backups found to prune.");
            return Task.FromResult(ExitCodes.Success);
        }

        consoleService.Info($"Found {history.Count} backup set(s).");

        if (history.Count <= options.Retention)
        {
            consoleService.Info($"All backups are within retention period (keeping {options.Retention}). Nothing to prune.");
            return Task.FromResult(ExitCodes.Success);
        }

        var toRemove = history.Count - options.Retention;
        consoleService.Warning($"Will remove {toRemove} oldest backup set(s).");

        if (!options.Quiet && !consoleService.AskConfirmation("Proceed with pruning?"))
        {
            consoleService.Info("Prune cancelled.");
            return Task.FromResult(ExitCodes.Success);
        }

        var result = backupManager.PruneBackups(backupPath, options.Retention);
        consoleService.Success($"Deleted {result.BackupsRemoved} backup set(s), {result.FilesRemoved} file(s), freed {result.BytesFreedFormatted}.");

        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
            {
                consoleService.Warning(error);
            }
        }

        return Task.FromResult(result.Success ? ExitCodes.Success : ExitCodes.FileOperationError);
    }

    /// <summary>
    /// Executes batch migration mode for multiple solutions.
    /// </summary>
    public static async Task<int> RunBatchModeAsync(
        Options options,
        IConsoleService consoleService,
        VersionResolver versionResolver,
        IBackupManager backupManager,
        ILoggerFactory? loggerFactory = null)
    {
        if (!ValidateOptions(options, consoleService))
        {
            return ExitCodes.ValidationError;
        }

        consoleService.WriteHeader();

        // Create batch service with a migration executor function
        var batchService = new BatchService(consoleService, async solutionOptions =>
        {
            var projectAnalyzer = new ProjectAnalyzer(consoleService, logger: loggerFactory?.CreateLogger<ProjectAnalyzer>());
            var migrationService = new MigrationService(
                consoleService,
                projectAnalyzer,
                versionResolver,
                null,
                backupManager,
                null,
                null,
                quietMode: options.Quiet,
                logger: loggerFactory?.CreateLogger<MigrationService>());

            return await migrationService.ExecuteAsync(solutionOptions);
        });

        var result = await batchService.RunBatchAsync(options);

        // Handle JSON output for batch mode
        await WriteJsonOutputIfRequested(options, result, consoleService);

        return result.Success ? ExitCodes.Success : ExitCodes.AnalysisIssuesFound;
    }

    /// <summary>
    /// Writes JSON output for batch results if requested.
    /// </summary>
    private static async Task WriteJsonOutputIfRequested(Options options, BatchResult result, IConsoleService consoleService)
    {
        if (options.Output != OutputFormat.Json)
        {
            return;
        }

        var formatter = new JsonFormatter();
        var output = formatter.Format(result);

        if (!string.IsNullOrEmpty(options.OutputFile))
        {
            await File.WriteAllTextAsync(options.OutputFile, output);
            if (!options.Quiet)
            {
                consoleService.Dim($"JSON output written to: {options.OutputFile}");
            }
        }
        else
        {
            Console.WriteLine(output);
        }
    }

    /// <summary>
    /// Executes standard migration, analysis, or rollback mode.
    /// </summary>
    public static async Task<int> RunMigrationAsync(
        Options options,
        IConsoleService consoleService,
        VersionResolver versionResolver,
        IBackupManager backupManager,
        ILoggerFactory? loggerFactory = null)
    {
        try
        {
            var projectAnalyzer = new ProjectAnalyzer(consoleService, logger: loggerFactory?.CreateLogger<ProjectAnalyzer>());
            var migrationService = new MigrationService(
                consoleService,
                projectAnalyzer,
                versionResolver,
                null,
                backupManager,
                null,
                null,
                quietMode: options.Quiet,
                logger: loggerFactory?.CreateLogger<MigrationService>());

            var result = await migrationService.ExecuteAsync(options);

            // Handle JSON output
            await WriteJsonOutputForMigration(options, result, consoleService);

            return result.ExitCode;
        }
        catch (IOException ex)
        {
            consoleService.Error($"\nFile operation error: {ex.Message}");
            await Console.Error.WriteLineAsync("\nSuggestion: Check file permissions and ensure no files are locked by another process.");
            return ExitCodes.FileOperationError;
        }
        catch (UnauthorizedAccessException ex)
        {
            consoleService.Error($"\nPermission denied: {ex.Message}");
            await Console.Error.WriteLineAsync("\nSuggestion: Run with elevated permissions or check file/folder access rights.");
            return ExitCodes.FileOperationError;
        }
        catch (Exception ex)
        {
            consoleService.Error($"\nUnexpected error: {ex.Message}");
#if DEBUG
            await Console.Error.WriteLineAsync(ex.StackTrace);
#endif
            await Console.Error.WriteLineAsync("\nSuggestion: Please report this issue at https://github.com/georgepwall1991/CPMigrate/issues");
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Writes JSON output for migration results if requested.
    /// </summary>
    private static async Task WriteJsonOutputForMigration(
        Options options,
        MigrationResult result,
        IConsoleService consoleService)
    {
        if (options.Output != OutputFormat.Json)
        {
            return;
        }

        var formatter = new JsonFormatter();
        string operation;
        if (options.Analyze)
        {
            operation = "analyze";
        }
        else if (options.Rollback)
        {
            operation = "rollback";
        }
        else
        {
            operation = "migrate";
        }

        var operationResult = new OperationResult
        {
            Operation = operation,
            Success = result.ExitCode == ExitCodes.Success,
            ExitCode = result.ExitCode,
            Summary = new OperationSummary
            {
                ProjectsProcessed = result.ProjectsProcessed,
                PackagesFound = result.PackagesCentralized,
                ConflictsResolved = result.ConflictsResolved
            },
            PropsFile = result.PropsFilePath != null ? new PropsFileInfo { Path = result.PropsFilePath } : null,
            DryRun = result.WasDryRun,
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        var output = formatter.Format(operationResult);

        if (!string.IsNullOrEmpty(options.OutputFile))
        {
            await File.WriteAllTextAsync(options.OutputFile, output);
            if (!options.Quiet)
            {
                consoleService.Dim($"JSON output written to: {options.OutputFile}");
            }
        }
        else
        {
            Console.WriteLine(output);
        }
    }
}
