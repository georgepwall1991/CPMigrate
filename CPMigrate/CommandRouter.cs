using CPMigrate.Models;
using CPMigrate.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CPMigrate;

/// <summary>
/// Routes and executes different command modes based on parsed options.
/// </summary>
#pragma warning disable CA1506 // CommandRouter is a dispatch table that inherently touches many types
internal static class CommandRouter
#pragma warning restore CA1506
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
            services
        );
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
        ILoggerFactory? loggerFactory = null
    )
    {
        var services = CreateApplicationServices(
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            loggerFactory
        );

        return await RouteCommand(
            options,
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            services
        );
    }

    private static async Task<int> RouteCommand(
        Options options,
        IConsoleService consoleService,
        IInteractiveService interactiveService,
        VersionResolver versionResolver,
        ConfigService configService,
        IBackupManager backupManager,
        ApplicationServices services
    )
    {
        var executionConsole = GetExecutionConsole(options, consoleService);

        // In JSON mode, swap in SilentConsoleService so dependency-discovery notices
        // ("Found project: …", banners) never leak into the JSON-only stdout contract.
        if (!ReferenceEquals(executionConsole, services.ConsoleService))
        {
            services = services.WithConsole(executionConsole);
        }

        // Pure-output commands run before anything else can write to stdout — including before the
        // config file is read, since `--completions zsh > _cpmigrate` is a documented redirection and
        // a "Loaded config from …" notice would land inside the script. ProgramRunner already handles
        // these; repeated here for callers that invoke the router directly.
        if (TryRunPureOutputCommand(options, out var pureOutputExitCode))
        {
            return pureOutputExitCode;
        }

        // Checked before dispatch, because the modes below run *instead* of an analysis: without this
        // `--update --output Sarif` would perform a real self-update and emit no SARIF, and
        // `--update --write-baseline` would record nothing.
        try
        {
            options.ValidateReportingContract();
        }
        catch (ArgumentException ex)
        {
            consoleService.Error(ex.Message);

            // The whole point of a machine-readable format is that a CI step parses stdout. A
            // rejection printed as prose there is a parse failure rather than a reported one, so
            // the consumer learns the run broke but not why — and this is the rejection most likely
            // to be hit in CI, since a rule ID is a string someone typed into a workflow file.
            await WriteErrorJsonOutputIfRequested(
                options,
                GetOperationName(options),
                ExitCodes.ValidationError,
                ex.Message
            );
            return ExitCodes.ValidationError;
        }

        var context = new CommandContext(
            options,
            consoleService,
            executionConsole,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            services
        );

        // Declarative rather than an if/else chain. The chain had grown to seven branches, and the
        // property this table makes checkable is precedence: `--update --interactive` has to resolve
        // the same way every time, and the order here is the whole specification of that.
        var selected = AlternateModes.FirstOrDefault(mode => mode.Matches(options));
        if (selected is not null)
        {
            return await selected.RunAsync(context);
        }

        // Check for updates in background for standard commands (if not quiet)
        Task? updateCheckTask = null;
        if (!options.Quiet && !options.Output.IsMachineReadable())
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

        var result = await RunMigrationAsync(
            options,
            executionConsole,
            versionResolver,
            backupManager,
            services
        );

        // Show update notification after main work completes to avoid interleaved output
        if (updateCheckTask != null)
        {
            try
            {
                var latestVersion = await (Task<NuGet.Versioning.NuGetVersion?>)updateCheckTask;
                if (latestVersion != null)
                {
                    consoleService.WriteLine();
                    consoleService.Warning(
                        $"A new version of CPMigrate is available: v{latestVersion}"
                    );
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
    /// Everything a mode handler might need. Passed as one object so the table's signature does not
    /// have to be the union of every handler's parameters.
    /// </summary>
    /// <param name="Options">Parsed options.</param>
    /// <param name="Console">The user's console, for messages that must appear even in JSON mode.</param>
    /// <param name="ExecutionConsole">Silent under a machine-readable format, so notices cannot leak into stdout.</param>
    /// <param name="Interactive">Wizard prompts.</param>
    /// <param name="VersionResolver">Version conflict resolution.</param>
    /// <param name="Config">Config file access.</param>
    /// <param name="Backups">Backup management.</param>
    /// <param name="Services">Service factory for the command being run.</param>
    private sealed record CommandContext(
        Options Options,
        IConsoleService Console,
        IConsoleService ExecutionConsole,
        IInteractiveService Interactive,
        VersionResolver VersionResolver,
        ConfigService Config,
        IBackupManager Backups,
        ApplicationServices Services
    );

    /// <summary>
    /// One dispatchable mode: what selects it, and what it runs.
    /// </summary>
    /// <param name="Matches">Whether the options select this mode.</param>
    /// <param name="RunAsync">The handler.</param>
    private sealed record CommandMode(
        Func<Options, bool> Matches,
        Func<CommandContext, Task<int>> RunAsync
    );

    /// <summary>
    /// Modes that run instead of the default migrate/analyze path, in precedence order.
    ///
    /// The order is the specification: when more than one flag is present — <c>--update</c> with
    /// <c>--interactive</c>, say — this list decides which wins, and it decided that implicitly when
    /// it was an if/else chain. As a table it can be asserted on, which is what
    /// <c>CommandRouterDispatchTests</c> does.
    /// </summary>
    private static readonly IReadOnlyList<CommandMode> AlternateModes =
    [
        new(o => o.Update, c => RunUpdateModeAsync(c.ExecutionConsole, c.Services)),
        new(
            o => o.UpdatePackages,
            c => RunUpdatePackagesModeAsync(c.Options, c.ExecutionConsole, c.Services)
        ),
        new(
            o => o.Interactive,
            c =>
                RunInteractiveModeAsync(
                    c.Console,
                    c.Interactive,
                    c.VersionResolver,
                    c.Config,
                    c.Backups
                )
        ),
        new(
            o => o.PruneBackups || o.PruneAll,
            c => RunPruneModeAsync(c.Options, c.ExecutionConsole, c.Backups)
        ),
        new(
            o => !string.IsNullOrEmpty(o.BatchDir),
            c =>
                RunBatchModeAsync(
                    c.Options,
                    c.ExecutionConsole,
                    c.VersionResolver,
                    c.Backups,
                    c.Services
                )
        ),
        new(
            o => o.UnifyProps,
            c => RunUnifyPropsModeAsync(c.Options, c.ExecutionConsole, c.Services)
        ),
    ];

    /// <summary>
    /// Handles the commands that only print something. Returns true when one ran, so the caller stops
    /// before touching the config file, the network, or the filesystem.
    /// </summary>
    private static bool TryRunPureOutputCommand(Options options, out int exitCode)
    {
        if (options.Completions.HasValue)
        {
            Console.WriteLine(CompletionScriptGenerator.Generate(options.Completions.Value));
            exitCode = ExitCodes.Success;
            return true;
        }

        if (options.Explain is not null)
        {
            var (explanation, found) = RuleExplainer.Explain(options.Explain);
            Console.WriteLine(explanation);
            exitCode = found ? ExitCodes.Success : ExitCodes.ValidationError;
            return true;
        }

        exitCode = ExitCodes.Success;
        return false;
    }

    private static IConsoleService GetExecutionConsole(
        Options options,
        IConsoleService consoleService
    )
    {
        return options.Output.IsMachineReadable() ? SilentConsoleService.Instance : consoleService;
    }

    private static bool ShouldSuppressHeadersAndBanners(Options options)
    {
        return options.Quiet || options.Output.IsMachineReadable();
    }

    /// <summary>
    /// Executes the self-update mode.
    /// </summary>
    private static async Task<int> RunUpdateModeAsync(
        IConsoleService consoleService,
        ApplicationServices services
    )
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
        ApplicationServices services
    )
    {
        if (!ValidateOptions(options, consoleService, out var validationError))
        {
            await WriteErrorJsonOutputIfRequested(
                options,
                "update-packages",
                ExitCodes.ValidationError,
                validationError ?? "Validation failed."
            );
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
            var result = await updateService.UpdatePackagesAsync(
                PackageUpdateRequest.FromOptions(options)
            );
            await WriteJsonOutputForPackageUpdate(options, result, consoleService);
            return result.ExitCode;
        }
        catch (IOException ex)
        {
            consoleService.Error($"\nFile operation error: {ex.Message}");
            await WriteErrorJsonOutputIfRequested(
                options,
                "update-packages",
                ExitCodes.FileOperationError,
                ex.Message
            );
            return ExitCodes.FileOperationError;
        }
        catch (Exception ex)
        {
            consoleService.Error($"\nUnexpected error: {ex.Message}");
            await WriteErrorJsonOutputIfRequested(
                options,
                "update-packages",
                ExitCodes.UnexpectedError,
                ex.Message
            );
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Executes the unify Directory.Build.props mode.
    /// </summary>
    public static async Task<int> RunUnifyPropsModeAsync(
        Options options,
        IConsoleService consoleService,
        ILoggerFactory? loggerFactory = null
    )
    {
        var executionConsole = GetExecutionConsole(options, consoleService);
        var services = CreateApplicationServices(
            executionConsole,
            new InteractiveService(consoleService),
            new VersionResolver(executionConsole),
            new ConfigService(executionConsole),
            new BackupManager(),
            loggerFactory
        );
        return await RunUnifyPropsModeAsync(options, consoleService, services);
    }

    private static async Task<int> RunUnifyPropsModeAsync(
        Options options,
        IConsoleService consoleService,
        ApplicationServices services
    )
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
        IBackupManager backupManager
    )
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

            var result = await ExecuteInteractiveCommand(
                options,
                consoleService,
                versionResolver,
                backupManager
            );

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
        IBackupManager backupManager
    )
    {
        var services = CreateApplicationServices(
            consoleService,
            new InteractiveService(consoleService),
            versionResolver,
            new ConfigService(consoleService),
            backupManager
        );

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

        return await RunMigrationAsync(
            options,
            consoleService,
            versionResolver,
            backupManager,
            services
        );
    }

    /// <summary>
    /// Executes backup pruning mode.
    /// </summary>
    public static async Task<int> RunPruneModeAsync(
        Options options,
        IConsoleService consoleService,
        IBackupManager backupManager
    )
    {
        if (!ValidateOptions(options, consoleService, out var validationError))
        {
            await WriteErrorJsonOutputIfRequested(
                options,
                options.PruneAll ? "prune-all-backups" : "prune-backups",
                ExitCodes.ValidationError,
                validationError ?? "Validation failed."
            );
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
    private static bool ValidateOptions(
        Options options,
        IConsoleService consoleService,
        out string? validationError
    )
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
        string backupPath
    )
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

        if (
            !ShouldProceedWithDestructiveAction(
                options,
                consoleService,
                "Are you sure you want to delete ALL backups?"
            )
        )
        {
            if (options.Output.IsMachineReadable())
            {
                return Task.FromResult(ExitCodes.ValidationError);
            }

            consoleService.Info("Prune cancelled.");
            return Task.FromResult(ExitCodes.Success);
        }

        var result = backupManager.PruneAllBackups(backupPath);
        consoleService.Success(
            $"Deleted {result.BackupsRemoved} backup set(s), {result.FilesRemoved} file(s), freed {result.BytesFreedFormatted}."
        );

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
        string backupPath
    )
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
            consoleService.Info(
                $"All backups are within retention period (keeping {options.Retention}). Nothing to prune."
            );
            return Task.FromResult(ExitCodes.Success);
        }

        var toRemove = history.Count - options.Retention;
        consoleService.Warning($"Will remove {toRemove} oldest backup set(s).");

        if (!ShouldProceedWithDestructiveAction(options, consoleService, "Proceed with pruning?"))
        {
            if (options.Output.IsMachineReadable())
            {
                return Task.FromResult(ExitCodes.ValidationError);
            }

            consoleService.Info("Prune cancelled.");
            return Task.FromResult(ExitCodes.Success);
        }

        var result = backupManager.PruneBackups(backupPath, options.Retention);
        consoleService.Success(
            $"Deleted {result.BackupsRemoved} backup set(s), {result.FilesRemoved} file(s), freed {result.BytesFreedFormatted}."
        );

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
        ILoggerFactory? loggerFactory = null
    )
    {
        var executionConsole = GetExecutionConsole(options, consoleService);
        var services = CreateApplicationServices(
            executionConsole,
            new InteractiveService(consoleService),
            versionResolver,
            new ConfigService(executionConsole),
            backupManager,
            loggerFactory
        );
        return await RunBatchModeAsync(
            options,
            consoleService,
            versionResolver,
            backupManager,
            services
        );
    }

    private static async Task<int> RunBatchModeAsync(
        Options options,
        IConsoleService consoleService,
        VersionResolver versionResolver,
        IBackupManager backupManager,
        ApplicationServices services
    )
    {
        _ = versionResolver;
        _ = backupManager;
        if (!ValidateOptions(options, consoleService, out var validationError))
        {
            await WriteErrorJsonOutputIfRequested(
                options,
                options.Analyze ? "batch-analyze" : "batch-migrate",
                ExitCodes.ValidationError,
                validationError ?? "Validation failed."
            );
            return ExitCodes.ValidationError;
        }

        if (!ShouldSuppressHeadersAndBanners(options))
        {
            consoleService.WriteHeader();
        }

        // Create batch service with a migration executor function
        var batchService = new BatchService(
            consoleService,
            async solutionOptions =>
            {
                var migrationService = services.CreateMigrationService(
                    solutionOptions.Quiet || solutionOptions.Output.IsMachineReadable()
                );
                return await migrationService.ExecuteAsync(solutionOptions);
            }
        );

        var result = await batchService.RunBatchAsync(options);

        // Handle JSON output for batch mode
        await WriteJsonOutputIfRequested(options, result, consoleService);

        return result.Success ? ExitCodes.Success : ExitCodes.AnalysisIssuesFound;
    }

    /// <summary>
    /// Writes JSON output for batch results if requested.
    /// </summary>
    private static async Task WriteJsonOutputIfRequested(
        Options options,
        BatchResult result,
        IConsoleService consoleService
    )
    {
        if (options.Output != OutputFormat.Json)
        {
            return;
        }

        var formatter = new JsonFormatter();
        var output = formatter.Format(result);

        await JsonOutputWriter.EmitAsync(output, options, consoleService);
    }

    private static bool ShouldProceedWithDestructiveAction(
        Options options,
        IConsoleService consoleService,
        string confirmationPrompt
    )
    {
        if (options.Force)
        {
            return true;
        }

        if (options.Output.IsMachineReadable())
        {
            return false;
        }

        if (options.Quiet)
        {
            return true;
        }

        // Backup deletion and pruning are unrecoverable. Neither --force nor --quiet was passed,
        // so nothing here signals intent to run unattended — decline rather than delete on a
        // terminal that cannot ask.
        if (!consoleService.IsInteractive)
        {
            consoleService.Warning("Cannot prompt for confirmation on a non-interactive terminal.");
            consoleService.Info("Re-run with --force to proceed without confirmation.");
            return false;
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
        ILoggerFactory? loggerFactory = null
    )
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
            loggerFactory
        );
        return await RunMigrationAsync(
            options,
            consoleService,
            versionResolver,
            backupManager,
            services
        );
    }

    private static async Task<int> RunMigrationAsync(
        Options options,
        IConsoleService consoleService,
        VersionResolver versionResolver,
        IBackupManager backupManager,
        ApplicationServices services
    )
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
                    validationError ?? "Validation failed."
                );
                return ExitCodes.ValidationError;
            }

            var migrationService = services.CreateMigrationService(
                options.Quiet || options.Output.IsMachineReadable()
            );
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
                ex.Message
            );
            if (!options.Output.IsMachineReadable() && !options.Quiet)
            {
                consoleService.WriteStructuredError(
                    "File operation failed",
                    ex.Message,
                    "Check file permissions and ensure no files are locked by another process."
                );
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
                ex.Message
            );
            if (!options.Output.IsMachineReadable() && !options.Quiet)
            {
                consoleService.WriteStructuredError(
                    "Permission denied",
                    ex.Message,
                    "Run with elevated permissions or check file/folder access rights."
                );
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
                ex.Message
            );
#if DEBUG
            if (!options.Output.IsMachineReadable() && !options.Quiet)
            {
                await Console.Error.WriteLineAsync(ex.StackTrace);
            }
#endif
            if (!options.Output.IsMachineReadable() && !options.Quiet)
            {
                consoleService.WriteStructuredError(
                    "Unexpected error",
                    ex.Message,
                    "Report this at https://github.com/georgepwall1991/CPMigrate/issues"
                );
            }
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Writes machine-readable output for migration results if requested. SARIF carries only
    /// analyzer findings, so it is emitted from a separate path rather than being squeezed into
    /// the general-purpose operation result.
    /// </summary>
    /// <summary>
    /// Emits the analysis as a SARIF 2.1.0 log. A run that produced no report (for example, no
    /// projects were discovered) still emits a valid empty log so a code-scanning upload step
    /// does not fail on a missing file.
    /// </summary>
    private static async Task WriteSarifOutputForMigration(
        Options options,
        MigrationResult result,
        IConsoleService consoleService
    )
    {
        var report =
            result.AnalysisReport ?? new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());
        var packageInfo =
            result.PackageInfo ?? new ProjectPackageInfo(Array.Empty<PackageReference>());
        var basePath = string.IsNullOrWhiteSpace(result.BasePath)
            ? Directory.GetCurrentDirectory()
            : result.BasePath;

        var sarif = SarifFormatter.Format(
            report,
            packageInfo,
            basePath,
            DescribeScanOutcome(result)
        );

        await JsonOutputWriter.EmitAsync(sarif, options, consoleService);
    }

    /// <summary>
    /// Emits the analysis as a Markdown report, for a CI job summary or a pull request comment.
    /// </summary>
    private static async Task WriteMarkdownOutputForMigration(
        Options options,
        MigrationResult result,
        IConsoleService consoleService
    )
    {
        // Report whatever the run produced, including the post-fix state when fixes were applied —
        // a reader wants to know what is in the tree now, not what was there before.
        var report =
            result.PostFixAnalysisReport
            ?? result.AnalysisReport
            ?? new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());
        var packageInfo =
            result.PackageInfo ?? new ProjectPackageInfo(Array.Empty<PackageReference>());

        var markdown = MarkdownFormatter.Format(
            report,
            packageInfo,
            new MarkdownReportContext(
                options.FailOn,
                result.GatedIssueCount,
                result.ExitCode,
                result.ScanFailures,
                result.DeepScanFailures,
                result.BaselinePath
                    ?? (options.UsesBaseline() ? options.ResolveBaselinePath() : null),
                result.BaselineWritten,
                result.ProjectsDiscovered > 0 ? result.ProjectsDiscovered : null
            )
        );

        await JsonOutputWriter.EmitAsync(markdown, options, consoleService);
    }

    private static async Task WriteCsvOutputAsync(
        Options options,
        MigrationResult result,
        IConsoleService consoleService
    )
    {
        var report =
            result.PostFixAnalysisReport
            ?? result.AnalysisReport
            ?? new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());

        var rows = report
            .Results.SelectMany(r =>
                r.Issues.Select(issue =>
                    string.Join(
                        ",",
                        CsvField(r.AnalyzerName),
                        CsvField(issue.Severity.ToString()),
                        CsvField(issue.PackageName),
                        CsvField(issue.Description),
                        CsvField(string.Join("; ", issue.AffectedProjects)),
                        issue.Fixable ? "true" : "false"
                    )
                )
            )
            .ToList();

        var csv =
            "Rule,Severity,Package,Description,AffectedProjects,Fixable\n"
            + string.Join("\n", rows)
            + "\n";

        await JsonOutputWriter.EmitAsync(csv, options, consoleService);
    }

    private static string CsvField(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    /// <summary>
    /// Decides whether the scan behind a result can be trusted. An empty finding list from a scan
    /// that never ran — or that failed on some projects — is a false negative, and reporting it as
    /// a successful run would let a code-scanning gate pass on unexamined code.
    /// </summary>
    private static SarifRunOutcome DescribeScanOutcome(MigrationResult result)
    {
        if (result.AnalysisReport is null)
        {
            return SarifRunOutcome.Failed("Analysis did not run: no projects were found to scan.");
        }

        if (result.ScanFailures > 0)
        {
            return SarifRunOutcome.Failed(
                $"{result.ScanFailures} of {result.ProjectsDiscovered} projects failed to scan; "
                    + "these findings are incomplete."
            );
        }

        if (result.DeepScanFailures > 0)
        {
            return SarifRunOutcome.Failed(
                $"{result.DeepScanFailures} package quer(ies) failed "
                    + "(--audit/--outdated/--deprecated); those findings are missing, not absent."
            );
        }

        return SarifRunOutcome.Successful;
    }

    /// <summary>
    /// The rule policy that actually shaped a payload — empty when no analysis ran. A migration
    /// under <c>--output Json</c> never applies the policy, so publishing it there would claim rules
    /// were switched off in a report they did not touch: the same lie as omitting it from a report
    /// they did, pointed the other way.
    /// </summary>
    private static RulePolicy PolicyThatShapedTheReport(Options options, AnalysisReport? report)
    {
        return report is null ? RulePolicy.Empty : options.ResolveRulePolicy();
    }

    private static async Task WriteJsonOutputForMigration(
        Options options,
        MigrationResult result,
        IConsoleService consoleService
    )
    {
        if (options.Output == OutputFormat.Sarif)
        {
            await WriteSarifOutputForMigration(options, result, consoleService);
            return;
        }

        if (options.Output == OutputFormat.Markdown)
        {
            await WriteMarkdownOutputForMigration(options, result, consoleService);
            return;
        }

        if (options.Output == OutputFormat.Csv)
        {
            await WriteCsvOutputAsync(options, result, consoleService);
            return;
        }

        if (options.Output != OutputFormat.Json)
        {
            return;
        }

        var formatter = new JsonFormatter();
        var operation = GetOperationName(options);
        var rulePolicy = PolicyThatShapedTheReport(options, result.AnalysisReport);

        var analysisIssues =
            result.AnalysisReport == null
                ? new List<AnalysisIssueInfo>()
                : result
                    .AnalysisReport.Results.SelectMany(analyzer =>
                        analyzer.Issues.Select(issue => new AnalysisIssueInfo
                        {
                            Type = analyzer.AnalyzerName,
                            IssueCode = issue.IssueCode.ToString(),
                            Severity = issue.Severity.ToString(),
                            Package = issue.PackageName,
                            Description = issue.Description,
                            AffectedProjects = issue.AffectedProjects.ToList(),
                            Fixable = issue.Fixable,
                            Suppressed = issue.Suppressed,
                            Metadata = issue.Metadata?.ToDictionary(
                                kvp => kvp.Key,
                                kvp => kvp.Value
                            ),
                        })
                    )
                    .ToList();

        var fixes =
            result.FixReport == null
                ? new List<FixInfo>()
                : result
                    .FixReport.Results.SelectMany(fixResult =>
                        fixResult.Changes.Select(change => new FixInfo
                        {
                            Type = fixResult.Description,
                            Package = string.Empty,
                            File = change.FilePath,
                            From = change.Before,
                            To = change.After,
                            Applied = fixResult.Success,
                        })
                    )
                    .ToList();

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
                FailOnSeverity = result.AnalysisReport is null ? null : options.FailOn.ToString(),
                IssuesAtOrAboveThreshold = result.GatedIssueCount,
                IssuesRemainingAfterFixes = result.PostFixAnalysisReport?.TotalIssues,
                IssuesBaselined = options.UsesBaseline()
                    ? result.AnalysisReport?.SuppressedCount
                    : null,
                DisabledRules = rulePolicy.ReportedDisabledRules(),
                SeverityOverrides = rulePolicy.ReportedSeverityOverrides(),
                HighestSeverity = result.AnalysisReport?.HighestSeverity?.ToString(),
                ScanFailures = result.AnalysisReport is null ? null : result.ScanFailures,
                DeepScanFailures = result.AnalysisReport is null ? null : result.DeepScanFailures,
                IssuesFixed = result.FixReport?.TotalFixesApplied ?? 0,
                FilesModified = result.FixReport?.TotalFileChanges ?? 0,
            },
            AnalysisIssues = analysisIssues,
            Fixes = fixes,
            PropsFile = string.IsNullOrWhiteSpace(result.PropsFilePath)
                ? null
                : new PropsFileInfo { Path = result.PropsFilePath },
            Backup = string.IsNullOrWhiteSpace(result.BackupPath)
                ? null
                : new BackupInfo { Path = result.BackupPath, FilesBackedUp = 0 },
            DryRun = result.WasDryRun,
            Timestamp = DateTime.UtcNow.ToString("o"),
        };

        var output = formatter.Format(operationResult);

        await JsonOutputWriter.EmitAsync(output, options, consoleService);
    }

    private static async Task WriteJsonOutputForPackageUpdate(
        Options options,
        PackageUpdateResult result,
        IConsoleService consoleService
    )
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
                WasRolledBack = result.WasRolledBack,
                PackagesHeldBack = result.PackagesHeldBack,
                VerificationRuns = result.VerificationRuns,
                BisectBudgetExhausted = result.BisectBudgetExhausted,
            },
            PackageUpdates = result
                .Updates.Select(update => new PackageUpdateInfo
                {
                    Package = update.PackageName,
                    CurrentVersion = update.CurrentVersion,
                    LatestVersion = update.LatestVersion,
                    IsMajorUpdate = update.IsMajorUpdate,
                    Accepted = update.Accepted,
                    Transitive = update.IsTransitive,
                    HeldBack = update.HeldBack,
                })
                .ToList(),
            DryRun = options.DryRun,
            Timestamp = DateTime.UtcNow.ToString("o"),
        };

        var output = formatter.Format(operationResult);

        await JsonOutputWriter.EmitAsync(output, options, consoleService);
    }

    private static async Task WriteErrorJsonOutputIfRequested(
        Options options,
        string operation,
        int exitCode,
        string errorMessage
    )
    {
        if (options.Output == OutputFormat.Markdown)
        {
            var report = $"## ❌ CPMigrate — {operation} failed\n\n{errorMessage}\n";
            await JsonOutputWriter.EmitFailureAsync(report, options);
            return;
        }

        if (options.Output == OutputFormat.Sarif)
        {
            // In SARIF mode stdout is a SARIF log unconditionally, so failures are reported as an
            // unsuccessful invocation rather than as CPMigrate's own error JSON.
            var sarif = SarifFormatter.FormatError(errorMessage, Directory.GetCurrentDirectory());
            await JsonOutputWriter.EmitFailureAsync(sarif, options);
            return;
        }

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
            Timestamp = DateTime.UtcNow.ToString("o"),
        };

        var output = formatter.Format(operationResult);

        await JsonOutputWriter.EmitFailureAsync(output, options);
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
        ILoggerFactory? loggerFactory = null
    )
    {
        var solutionDiscovery = new SolutionDiscovery(consoleService);
        var projectFileScanner = new ProjectFileScanner(consoleService);
        var packageQueryService = new DotNetPackageQueryService(consoleService);
        var projectAnalyzer = new ProjectAnalyzer(
            consoleService,
            solutionDiscovery,
            projectFileScanner,
            packageQueryService,
            loggerFactory?.CreateLogger<ProjectAnalyzer>()
        );

        return new ApplicationServices(
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            projectAnalyzer,
            loggerFactory
        );
    }
}
