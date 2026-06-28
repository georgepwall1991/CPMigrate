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
    public static Task<int> RouteCommand(Options options, ApplicationServices services)
    {
        return RouteCommand(
            options,
            services.ConsoleService,
            services.InteractiveService,
            services.VersionResolver,
            services.ConfigService,
            services.BackupManager,
            services);
    }

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
        var services = CreateApplicationServices(
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            loggerFactory);

        return await RouteCommand(
            options,
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            services);
    }

    private static async Task<int> RouteCommand(
        Options options,
        IConsoleService consoleService,
        IInteractiveService interactiveService,
        VersionResolver versionResolver,
        ConfigService configService,
        IBackupManager backupManager,
        ApplicationServices services)
    {
        var executionConsole = GetExecutionConsole(options, consoleService);

        // In JSON mode, swap in SilentConsoleService so dependency-discovery notices
        // ("Found project: …", banners) never leak into the JSON-only stdout contract.
        if (!ReferenceEquals(executionConsole, services.ConsoleService))
        {
            services = services.WithConsole(executionConsole);
        }

        // Handle Update command
        if (options.Update)
        {
            return await RunUpdateModeAsync(executionConsole, services);
        }

        // Handle Update Packages command
        if (options.UpdatePackages)
        {
            return await RunUpdatePackagesModeAsync(options, executionConsole, services);
        }

        // Route to appropriate command handler
        if (options.Interactive)
        {
            return await RunInteractiveModeAsync(consoleService, interactiveService, versionResolver, configService, backupManager);
        }

        if (options.PruneBackups || options.PruneAll)
        {
            return await RunPruneModeAsync(options, executionConsole, backupManager);
        }

        if (!string.IsNullOrEmpty(options.BatchDir))
        {
            return await RunBatchModeAsync(options, executionConsole, versionResolver, backupManager, services);
        }

        if (options.UnifyProps)
        {
            return await RunUnifyPropsModeAsync(options, executionConsole, services);
        }

        // Check for updates in background for standard commands (if not quiet)
        Task? updateCheckTask = null;
        if (!options.Quiet && !options.Output.Equals(OutputFormat.Json))
        {
            updateCheckTask = Task.Run(async () =>
            {
                using var updateService = services.CreateUpdateService();
                var latestVersion = await updateService.CheckForUpdatesAsync();
                if (latestVersion != null)
                {
                    return latestVersion;
                }
                return null;
            });
        }

        var result = await RunMigrationAsync(options, executionConsole, versionResolver, backupManager, services);

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

    private static IConsoleService GetExecutionConsole(Options options, IConsoleService consoleService)
    {
        return options.Output == OutputFormat.Json ? SilentConsoleService.Instance : consoleService;
    }

    private static bool ShouldSuppressHeadersAndBanners(Options options)
    {
        return options.Quiet || options.Output == OutputFormat.Json;
    }

    /// <summary>
    /// Executes the self-update mode.
    /// </summary>
    private static async Task<int> RunUpdateModeAsync(IConsoleService consoleService, ApplicationServices services)
    {
        try
        {
            using var updateService = services.CreateUpdateService();
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
    /// Executes the update-packages mode: updates NuGet packages to latest, runs tests, rolls back on failure.
    /// </summary>
    private static async Task<int> RunUpdatePackagesModeAsync(
        Options options,
        IConsoleService consoleService,
        ApplicationServices services)
    {
        if (!ValidateOptions(options, consoleService, out var validationError))
        {
            await WriteErrorJsonOutputIfRequested(
                options,
                "update-packages",
                ExitCodes.ValidationError,
                validationError ?? "Validation failed.");
            return ExitCodes.ValidationError;
        }

        if (!ShouldSuppressHeadersAndBanners(options))
        {
            consoleService.WriteHeader();
            consoleService.Banner("UPDATE PACKAGES");
            consoleService.WriteLine();
        }

        try
        {
            using var updateService = services.CreatePackageUpdateService();
            var result = await updateService.UpdatePackagesAsync(PackageUpdateRequest.FromOptions(options));
            await WriteJsonOutputForPackageUpdate(options, result, consoleService);
            return result.ExitCode;
        }
        catch (IOException ex)
        {
            consoleService.Error($"\nFile operation error: {ex.Message}");
            await WriteErrorJsonOutputIfRequested(options, "update-packages", ExitCodes.FileOperationError, ex.Message);
            return ExitCodes.FileOperationError;
        }
        catch (Exception ex)
        {
            consoleService.Error($"\nUnexpected error: {ex.Message}");
            await WriteErrorJsonOutputIfRequested(options, "update-packages", ExitCodes.UnexpectedError, ex.Message);
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Executes the unify Directory.Build.props mode.
    /// </summary>
    public static async Task<int> RunUnifyPropsModeAsync(Options options, IConsoleService consoleService, ILoggerFactory? loggerFactory = null)
    {
        var executionConsole = GetExecutionConsole(options, consoleService);
        var services = CreateApplicationServices(
            executionConsole,
            new InteractiveService(consoleService),
            new VersionResolver(executionConsole),
            new ConfigService(executionConsole),
            new BackupManager(),
            loggerFactory);
        return await RunUnifyPropsModeAsync(options, consoleService, services);
    }

    private static async Task<int> RunUnifyPropsModeAsync(Options options, IConsoleService consoleService, ApplicationServices services)
    {
        try
        {
            if (!ShouldSuppressHeadersAndBanners(options))
            {
                consoleService.WriteHeader();
            }

            var buildPropsService = services.CreateBuildPropsService();
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
            var startDir = options.GetConfigSearchStartDirectory();
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
        var services = CreateApplicationServices(
            consoleService,
            new InteractiveService(consoleService),
            versionResolver,
            new ConfigService(consoleService),
            backupManager);

        if (options.UpdatePackages)
        {
            return await RunUpdatePackagesModeAsync(options, consoleService, services);
        }

        if (!string.IsNullOrEmpty(options.BatchDir))
        {
            return await RunBatchModeAsync(options, consoleService, versionResolver, backupManager);
        }

        if (options.PruneBackups || options.PruneAll || options.ListBackups)
        {
            if (options.ListBackups)
            {
                var migrationService = services.CreateMigrationService(options.Quiet);
                var migrationResult = await migrationService.ExecuteAsync(options);
                return migrationResult.ExitCode;
            }

            return await RunPruneModeAsync(options, consoleService, backupManager);
        }

        return await RunMigrationAsync(options, consoleService, versionResolver, backupManager, services);
    }

    /// <summary>
    /// Executes backup pruning mode.
    /// </summary>
    public static async Task<int> RunPruneModeAsync(
        Options options,
        IConsoleService consoleService,
        IBackupManager backupManager)
    {
        if (!ValidateOptions(options, consoleService, out var validationError))
        {
            await WriteErrorJsonOutputIfRequested(
                options,
                options.PruneAll ? "prune-all-backups" : "prune-backups",
                ExitCodes.ValidationError,
                validationError ?? "Validation failed.");
            return ExitCodes.ValidationError;
        }

        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        if (!Directory.Exists(backupPath))
        {
            consoleService.Error($"No backup directory found at: {backupPath}");
            return ExitCodes.FileOperationError;
        }

        if (!ShouldSuppressHeadersAndBanners(options))
        {
            consoleService.WriteHeader();
        }

        if (options.PruneAll)
        {
            return await PruneAllBackupsAsync(options, consoleService, backupManager, backupPath);
        }

        return await PruneOldBackupsAsync(options, consoleService, backupManager, backupPath);
    }

    /// <summary>
    /// Validates options and shows error if validation fails.
    /// </summary>
    private static bool ValidateOptions(Options options, IConsoleService consoleService, out string? validationError)
    {
        validationError = null;
        try
        {
            options.Validate();
            return true;
        }
        catch (ArgumentException ex)
        {
            validationError = ex.Message;
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
        if (!ShouldSuppressHeadersAndBanners(options))
        {
            consoleService.Banner("PRUNE ALL BACKUPS");
            consoleService.WriteLine();
        }

        var history = backupManager.GetBackupHistory(backupPath);
        if (history.Count == 0)
        {
            consoleService.Info("No backups found to delete.");
            return Task.FromResult(ExitCodes.Success);
        }

        consoleService.Warning($"This will delete ALL {history.Count} backup set(s).");

        if (!ShouldProceedWithDestructiveAction(options, consoleService, "Are you sure you want to delete ALL backups?"))
        {
            if (options.Output == OutputFormat.Json)
            {
                return Task.FromResult(ExitCodes.ValidationError);
            }

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
        if (!ShouldSuppressHeadersAndBanners(options))
        {
            consoleService.Banner($"PRUNE BACKUPS - Keeping last {options.Retention}");
            consoleService.WriteLine();
        }

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

        if (!ShouldProceedWithDestructiveAction(options, consoleService, "Proceed with pruning?"))
        {
            if (options.Output == OutputFormat.Json)
            {
                return Task.FromResult(ExitCodes.ValidationError);
            }

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
        var executionConsole = GetExecutionConsole(options, consoleService);
        var services = CreateApplicationServices(
            executionConsole,
            new InteractiveService(consoleService),
            versionResolver,
            new ConfigService(executionConsole),
            backupManager,
            loggerFactory);
        return await RunBatchModeAsync(options, consoleService, versionResolver, backupManager, services);
    }

    private static async Task<int> RunBatchModeAsync(
        Options options,
        IConsoleService consoleService,
        VersionResolver versionResolver,
        IBackupManager backupManager,
        ApplicationServices services)
    {
        _ = versionResolver;
        _ = backupManager;
        if (!ValidateOptions(options, consoleService, out var validationError))
        {
            await WriteErrorJsonOutputIfRequested(
                options,
                options.Analyze ? "batch-analyze" : "batch-migrate",
                ExitCodes.ValidationError,
                validationError ?? "Validation failed.");
            return ExitCodes.ValidationError;
        }

        if (!ShouldSuppressHeadersAndBanners(options))
        {
            consoleService.WriteHeader();
        }

        // Create batch service with a migration executor function
        var batchService = new BatchService(consoleService, async solutionOptions =>
        {
            var migrationService = services.CreateMigrationService(solutionOptions.Quiet || solutionOptions.Output == OutputFormat.Json);
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

        await JsonOutputWriter.EmitAsync(output, options, consoleService);
    }

    private static bool ShouldProceedWithDestructiveAction(Options options, IConsoleService consoleService, string confirmationPrompt)
    {
        if (options.Force)
        {
            return true;
        }

        if (options.Output == OutputFormat.Json)
        {
            return false;
        }

        if (options.Quiet)
        {
            return true;
        }

        return consoleService.AskConfirmation(confirmationPrompt);
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
        if (options is null)
        {
            return ExitCodes.UnexpectedError;
        }

        var executionConsole = GetExecutionConsole(options, consoleService);
        var services = CreateApplicationServices(
            executionConsole,
            new InteractiveService(consoleService),
            versionResolver,
            new ConfigService(executionConsole),
            backupManager,
            loggerFactory);
        return await RunMigrationAsync(options, consoleService, versionResolver, backupManager, services);
    }

    private static async Task<int> RunMigrationAsync(
        Options options,
        IConsoleService consoleService,
        VersionResolver versionResolver,
        IBackupManager backupManager,
        ApplicationServices services)
    {
        _ = versionResolver;
        _ = backupManager;
        if (options is null)
        {
            return ExitCodes.UnexpectedError;
        }

        try
        {
            if (!ValidateOptions(options, consoleService, out var validationError))
            {
                await WriteErrorJsonOutputIfRequested(
                    options,
                    GetOperationName(options),
                    ExitCodes.ValidationError,
                    validationError ?? "Validation failed.");
                return ExitCodes.ValidationError;
            }

            var migrationService = services.CreateMigrationService(options.Quiet || options.Output == OutputFormat.Json);
            var result = await migrationService.ExecuteAsync(options);

            // Handle JSON output
            await WriteJsonOutputForMigration(options, result, consoleService);

            return result.ExitCode;
        }
        catch (IOException ex)
        {
            consoleService.Error($"\nFile operation error: {ex.Message}");
            await WriteErrorJsonOutputIfRequested(
                options,
                GetOperationName(options),
                ExitCodes.FileOperationError,
                ex.Message);
            if (options.Output != OutputFormat.Json && !options.Quiet)
            {
                await Console.Error.WriteLineAsync("\nSuggestion: Check file permissions and ensure no files are locked by another process.");
            }
            return ExitCodes.FileOperationError;
        }
        catch (UnauthorizedAccessException ex)
        {
            consoleService.Error($"\nPermission denied: {ex.Message}");
            await WriteErrorJsonOutputIfRequested(
                options,
                GetOperationName(options),
                ExitCodes.FileOperationError,
                ex.Message);
            if (options.Output != OutputFormat.Json && !options.Quiet)
            {
                await Console.Error.WriteLineAsync("\nSuggestion: Run with elevated permissions or check file/folder access rights.");
            }
            return ExitCodes.FileOperationError;
        }
        catch (Exception ex)
        {
            consoleService.Error($"\nUnexpected error: {ex.Message}");
            await WriteErrorJsonOutputIfRequested(
                options,
                GetOperationName(options),
                ExitCodes.UnexpectedError,
                ex.Message);
#if DEBUG
            if (options.Output != OutputFormat.Json && !options.Quiet)
            {
                await Console.Error.WriteLineAsync(ex.StackTrace);
            }
#endif
            if (options.Output != OutputFormat.Json && !options.Quiet)
            {
                await Console.Error.WriteLineAsync("\nSuggestion: Please report this issue at https://github.com/georgepwall1991/CPMigrate/issues");
            }
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
        var operation = GetOperationName(options);

        var analysisIssues = result.AnalysisReport == null
            ? new List<AnalysisIssueInfo>()
            : result.AnalysisReport.Results
                .SelectMany(analyzer => analyzer.Issues.Select(issue => new AnalysisIssueInfo
                {
                    Type = analyzer.AnalyzerName,
                    IssueCode = issue.IssueCode.ToString(),
                    Severity = issue.Severity.ToString(),
                    Package = issue.PackageName,
                    Description = issue.Description,
                    AffectedProjects = issue.AffectedProjects.ToList(),
                    Fixable = issue.Fixable,
                    Metadata = issue.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                }))
                .ToList();

        var fixes = result.FixReport == null
            ? new List<FixInfo>()
            : result.FixReport.Results.SelectMany(fixResult =>
                fixResult.Changes.Select(change => new FixInfo
                {
                    Type = fixResult.Description,
                    Package = string.Empty,
                    File = change.FilePath,
                    From = change.Before,
                    To = change.After,
                    Applied = fixResult.Success
                })).ToList();

        var operationResult = new OperationResult
        {
            Operation = operation,
            Success = result.ExitCode == ExitCodes.Success,
            ExitCode = result.ExitCode,
            Summary = new OperationSummary
            {
                ProjectsProcessed = result.ProjectsProcessed,
                PackagesFound = result.PackagesCentralized,
                ConflictsResolved = result.ConflictsResolved,
                IssuesFound = result.AnalysisReport?.TotalIssues ?? 0,
                IssuesFixed = result.FixReport?.TotalFixesApplied ?? 0,
                FilesModified = result.FixReport?.TotalFileChanges ?? 0
            },
            AnalysisIssues = analysisIssues,
            Fixes = fixes,
            PropsFile = string.IsNullOrWhiteSpace(result.PropsFilePath) ? null : new PropsFileInfo { Path = result.PropsFilePath },
            Backup = string.IsNullOrWhiteSpace(result.BackupPath) ? null : new BackupInfo { Path = result.BackupPath, FilesBackedUp = 0 },
            DryRun = result.WasDryRun,
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        var output = formatter.Format(operationResult);

        await JsonOutputWriter.EmitAsync(output, options, consoleService);
    }

    private static async Task WriteJsonOutputForPackageUpdate(
        Options options,
        PackageUpdateResult result,
        IConsoleService consoleService)
    {
        if (options.Output != OutputFormat.Json)
        {
            return;
        }

        var formatter = new JsonFormatter();
        var operationResult = new OperationResult
        {
            Operation = "update-packages",
            Success = result.ExitCode == ExitCodes.Success,
            ExitCode = result.ExitCode,
            Summary = new OperationSummary
            {
                PackagesChecked = result.PackagesChecked,
                PackagesUpdated = result.PackagesUpdated,
                PackagesSkipped = result.PackagesSkipped,
                TransitivePackagesFound = result.TransitivePackagesFound,
                TransitivePackagesUpdated = result.TransitivePackagesUpdated,
                TestsPassed = result.TestsPassed,
                WasRolledBack = result.WasRolledBack
            },
            PackageUpdates = result.Updates.Select(update => new PackageUpdateInfo
            {
                Package = update.PackageName,
                CurrentVersion = update.CurrentVersion,
                LatestVersion = update.LatestVersion,
                IsMajorUpdate = update.IsMajorUpdate,
                Accepted = update.Accepted,
                Transitive = update.IsTransitive
            }).ToList(),
            DryRun = options.DryRun,
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        var output = formatter.Format(operationResult);

        await JsonOutputWriter.EmitAsync(output, options, consoleService);
    }

    private static async Task WriteErrorJsonOutputIfRequested(
        Options options,
        string operation,
        int exitCode,
        string errorMessage)
    {
        if (options.Output != OutputFormat.Json)
        {
            return;
        }

        var formatter = new JsonFormatter();
        var operationResult = new OperationResult
        {
            Operation = operation,
            Success = false,
            ExitCode = exitCode,
            Errors = new List<string> { errorMessage },
            DryRun = options.DryRun,
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        var output = formatter.Format(operationResult);

        await JsonOutputWriter.EmitAsync(output, options, null, announceFile: false);
    }

    private static string GetOperationName(Options options)
    {
        if (options.Analyze)
        {
            return "analyze";
        }

        if (options.Rollback)
        {
            return "rollback";
        }

        return "migrate";
    }

    private static ApplicationServices CreateApplicationServices(
        IConsoleService consoleService,
        IInteractiveService interactiveService,
        VersionResolver versionResolver,
        ConfigService configService,
        IBackupManager backupManager,
        ILoggerFactory? loggerFactory = null)
    {
        var solutionDiscovery = new SolutionDiscovery(consoleService);
        var projectFileScanner = new ProjectFileScanner(consoleService);
        var packageQueryService = new DotNetPackageQueryService(consoleService);
        var projectAnalyzer = new ProjectAnalyzer(
            consoleService,
            solutionDiscovery,
            projectFileScanner,
            packageQueryService,
            loggerFactory?.CreateLogger<ProjectAnalyzer>());

        return new ApplicationServices(
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            projectAnalyzer,
            loggerFactory);
    }
}
