using CommandLine;
using CPMigrate;
using CPMigrate.Services;

// Setup composition root - create console service first, then inject into version resolver
var versionResolver = new VersionResolver(null); // Will be re-created with console service in MigrationService
var consoleService = new SpectreConsoleService(versionResolver);
var interactiveService = new InteractiveService(consoleService);

// Check for interactive mode (no args or --interactive flag)
if (args.Length == 0)
{
    return await RunInteractiveMode(consoleService, interactiveService, versionResolver);
}

return await Parser.Default.ParseArguments<Options>(args)
    .MapResult(
        async opt =>
        {
            // Check if --interactive flag was passed
            if (opt.Interactive)
            {
                return await RunInteractiveMode(consoleService, interactiveService, versionResolver);
            }
            return await RunMigration(opt, consoleService, versionResolver);
        },
        _ => Task.FromResult(ExitCodes.ValidationError));

static async Task<int> RunInteractiveMode(IConsoleService consoleService, IInteractiveService interactiveService, VersionResolver versionResolver)
{
    consoleService.WriteHeader();

    var options = interactiveService.RunWizard();
    if (options == null)
    {
        return ExitCodes.Success; // User cancelled
    }

    return await RunMigration(options, consoleService, versionResolver);
}

static async Task<int> RunMigration(Options opt, IConsoleService consoleService, VersionResolver versionResolver)
{
    try
    {
        // Manual Dependency Injection
        var migrationService = new MigrationService(consoleService, null, versionResolver);
        var result = await migrationService.ExecuteAsync(opt);
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