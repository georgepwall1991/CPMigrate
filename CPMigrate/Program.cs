using CommandLine;
using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;

// Setup composition root - create console service first, then inject into version resolver
var versionResolver = new VersionResolver(null); // Will be re-created with console service in MigrationService
var consoleService = new SpectreConsoleService(versionResolver);
var interactiveService = new InteractiveService(consoleService);
var configService = new ConfigService(consoleService);
var backupManager = new BackupManager();

// Check for interactive mode (no args or --interactive flag)
if (args.Length == 0)
{
    return await RunInteractiveMode(consoleService, interactiveService, versionResolver, configService, backupManager);
}

return await Parser.Default.ParseArguments<Options>(args)
    .MapResult(
        async opt =>
        {
            // Load config file and merge (CLI args take precedence)
            var startDir = !string.IsNullOrEmpty(opt.BatchDir) ? opt.BatchDir :
                           !string.IsNullOrEmpty(opt.SolutionFileDir) && opt.SolutionFileDir != "." ? opt.SolutionFileDir :
                           !string.IsNullOrEmpty(opt.ProjectFileDir) ? opt.ProjectFileDir : ".";
            var config = configService.LoadConfig(startDir);
            if (config != null)
            {
                // Determine which CLI args were explicitly provided
                var cliArgsProvided = GetExplicitCliArgs(args);
                configService.MergeConfig(opt, config, cliArgsProvided);
            }

            // Check if --interactive flag was passed
            if (opt.Interactive)
            {
                return await RunInteractiveMode(consoleService, interactiveService, versionResolver, configService, backupManager);
            }

            // Handle backup pruning mode
            if (opt.PruneBackups || opt.PruneAll)
            {
                return await RunPruneMode(opt, consoleService, backupManager);
            }

            // Handle batch mode
            if (!string.IsNullOrEmpty(opt.BatchDir))
            {
                return await RunBatchMode(opt, consoleService, versionResolver, backupManager);
            }

            // Handle unify props mode
            if (opt.UnifyProps)
            {
                return await RunUnifyPropsMode(opt, consoleService);
            }

            return await RunMigration(opt, consoleService, versionResolver, backupManager);
        },
        _ => Task.FromResult(ExitCodes.ValidationError));

static async Task<int> RunUnifyPropsMode(Options opt, IConsoleService consoleService)
{
    try
    {
        consoleService.WriteHeader();
        
        var projectAnalyzer = new ProjectAnalyzer(consoleService);
        var buildPropsService = new BuildPropsService(consoleService, projectAnalyzer);
        
        return await buildPropsService.UnifyPropertiesAsync(opt);
    }
    catch (Exception ex)
    {
        consoleService.Error($"\nUnexpected error: {ex.Message}");
        return ExitCodes.UnexpectedError;
    }
}

static HashSet<string> GetExplicitCliArgs(string[] args)
{
    var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var arg in args)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            var name = arg[2..];
            var equalsIndex = name.IndexOf('=');
            if (equalsIndex > 0)
            {
                name = name[..equalsIndex];
            }
            provided.Add(name);
        }
        else if (arg.StartsWith('-') && arg.Length == 2)
        {
            // Map short options to long names
            var shortOpt = arg[1];
            var longName = shortOpt switch
            {
                's' => "solution",
                'p' => "project",
                'o' => "output-dir",
                'k' => "keep-attrs",
                'n' => "no-backup",
                'd' => "dry-run",
                'r' => "rollback",
                'a' => "analyze",
                'i' => "interactive",
                'q' => "quiet",
                _ => null
            };
            if (longName != null)
            {
                provided.Add(longName);
            }
        }
    }
    return provided;
}

static async Task<int> RunInteractiveMode(IConsoleService consoleService, IInteractiveService interactiveService,
    VersionResolver versionResolver, ConfigService configService, BackupManager backupManager)
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
        var startDir = !string.IsNullOrEmpty(options.BatchDir) ? options.BatchDir :
                       !string.IsNullOrEmpty(options.SolutionFileDir) && options.SolutionFileDir != "." ? options.SolutionFileDir :
                       !string.IsNullOrEmpty(options.ProjectFileDir) ? options.ProjectFileDir : ".";
        var config = configService.LoadConfig(startDir);
        if (config != null)
        {
            configService.MergeConfig(options, config);
        }

        int result;
        if (!string.IsNullOrEmpty(options.BatchDir))
        {
            result = await RunBatchMode(options, consoleService, versionResolver, backupManager);
        }
        else if (options.PruneBackups || options.PruneAll || options.ListBackups)
        {
            if (options.ListBackups)
            {
                var migrationService = new MigrationService(consoleService, null, versionResolver, null, backupManager, null, options.Quiet);
                var migrationResult = await migrationService.ExecuteAsync(options);
                result = migrationResult.ExitCode;
            }
            else
            {
                result = await RunPruneMode(options, consoleService, backupManager);
            }
        }
        else
        {
            result = await RunMigration(options, consoleService, versionResolver, backupManager);
        }

        // Show result and prompt to continue
        consoleService.WriteLine();
        if (!consoleService.AskConfirmation("Return to main menu?"))
        {
            return result;
        }

        consoleService.WriteLine();
    }
}

static async Task<int> RunPruneMode(Options opt, IConsoleService consoleService, BackupManager backupManager)
{
    try
    {
        opt.Validate();
    }
    catch (ArgumentException ex)
    {
        consoleService.Error(ex.Message);
        return ExitCodes.ValidationError;
    }

    var backupPath = backupManager.GetBackupDirectoryPath(opt);

    if (!Directory.Exists(backupPath))
    {
        consoleService.Error($"No backup directory found at: {backupPath}");
        return ExitCodes.FileOperationError;
    }

    consoleService.WriteHeader();

    if (opt.PruneAll)
    {
        consoleService.Banner("PRUNE ALL BACKUPS");
        consoleService.WriteLine();

        var history = backupManager.GetBackupHistory(backupPath);
        if (history.Count == 0)
        {
            consoleService.Info("No backups found to delete.");
            return ExitCodes.Success;
        }

        consoleService.Warning($"This will delete ALL {history.Count} backup set(s).");

        if (!opt.Quiet && !consoleService.AskConfirmation("Are you sure you want to delete ALL backups?"))
        {
            consoleService.Info("Prune cancelled.");
            return ExitCodes.Success;
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

        return result.Success ? ExitCodes.Success : ExitCodes.FileOperationError;
    }
    else // PruneBackups
    {
        consoleService.Banner($"PRUNE BACKUPS - Keeping last {opt.Retention}");
        consoleService.WriteLine();

        var history = backupManager.GetBackupHistory(backupPath);
        if (history.Count == 0)
        {
            consoleService.Info("No backups found to prune.");
            return ExitCodes.Success;
        }

        consoleService.Info($"Found {history.Count} backup set(s).");

        if (history.Count <= opt.Retention)
        {
            consoleService.Info($"All backups are within retention limit ({opt.Retention}). Nothing to prune.");
            return ExitCodes.Success;
        }

        var toRemove = history.Count - opt.Retention;
        consoleService.Info($"Will remove {toRemove} old backup set(s).");

        var result = backupManager.PruneBackups(backupPath, opt.Retention);
        consoleService.Success($"Pruned {result.BackupsRemoved} backup set(s), {result.FilesRemoved} file(s), freed {result.BytesFreedFormatted}.");
        consoleService.Dim($"Kept {result.KeptCount} most recent backup(s).");

        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
            {
                consoleService.Warning(error);
            }
        }

        return result.Success ? ExitCodes.Success : ExitCodes.FileOperationError;
    }
}

static async Task<int> RunBatchMode(Options opt, IConsoleService consoleService, VersionResolver versionResolver, BackupManager backupManager)
{
    try
    {
        opt.Validate();
    }
    catch (ArgumentException ex)
    {
        consoleService.Error(ex.Message);
        return ExitCodes.ValidationError;
    }

    consoleService.WriteHeader();

    // Create batch service with a migration executor function
    var batchService = new BatchService(consoleService, async solutionOptions =>
    {
        var migrationService = new MigrationService(consoleService, null, versionResolver, null, backupManager, null, opt.Quiet);
        return await migrationService.ExecuteAsync(solutionOptions);
    });

    var result = await batchService.RunBatchAsync(opt);

    // Handle JSON output for batch mode
    if (opt.Output == OutputFormat.Json)
    {
        var formatter = new JsonFormatter();
        var output = formatter.Format(result);

        if (!string.IsNullOrEmpty(opt.OutputFile))
        {
            await File.WriteAllTextAsync(opt.OutputFile, output);
            if (!opt.Quiet)
            {
                consoleService.Dim($"JSON output written to: {opt.OutputFile}");
            }
        }
        else
        {
            Console.WriteLine(output);
        }
    }

    return result.Success ? ExitCodes.Success : ExitCodes.AnalysisIssuesFound;
}

static async Task<int> RunMigration(Options opt, IConsoleService consoleService, VersionResolver versionResolver, BackupManager backupManager)
{
    try
    {
        // Manual Dependency Injection
        var migrationService = new MigrationService(consoleService, null, versionResolver, null, backupManager, null, opt.Quiet);
        var result = await migrationService.ExecuteAsync(opt);

        // Handle JSON output
        if (opt.Output == OutputFormat.Json)
        {
            var formatter = new JsonFormatter();
            var operationResult = new OperationResult
            {
                Operation = opt.Analyze ? "analyze" : opt.Rollback ? "rollback" : "migrate",
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

            if (!string.IsNullOrEmpty(opt.OutputFile))
            {
                await File.WriteAllTextAsync(opt.OutputFile, output);
                if (!opt.Quiet)
                {
                    consoleService.Dim($"JSON output written to: {opt.OutputFile}");
                }
            }
            else
            {
                Console.WriteLine(output);
            }
        }

        return result.ExitCode;
    }
    catch (IOException ex)
    {
        consoleService.Error($"\nFile operation error: {ex.Message}");
        Console.Error.WriteLine("\nSuggestion: Check file permissions and ensure no files are locked by another process.");
        return ExitCodes.FileOperationError;
    }
    catch (UnauthorizedAccessException ex)
    {
        consoleService.Error($"\nPermission denied: {ex.Message}");
        Console.Error.WriteLine("\nSuggestion: Run with elevated permissions or check file/folder access rights.");
        return ExitCodes.FileOperationError;
    }
    catch (Exception ex)
    {
        consoleService.Error($"\nUnexpected error: {ex.Message}");
#if DEBUG
        Console.Error.WriteLine(ex.StackTrace);
#endif
        Console.Error.WriteLine("\nSuggestion: Please report this issue at https://github.com/georgepwall1991/CPMigrate/issues");
        return ExitCodes.UnexpectedError;
    }
}