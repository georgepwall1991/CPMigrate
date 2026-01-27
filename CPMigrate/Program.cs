using CommandLine;
using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;

// Setup composition root
var versionResolver = new VersionResolver(null);
var consoleService = new SpectreConsoleService(versionResolver);
var interactiveService = new InteractiveService(consoleService);
var configService = new ConfigService(consoleService);
var backupManager = new BackupManager();

// Check for interactive mode (no args or --interactive flag)
if (args.Length == 0)
{
    return await CommandRouter.RunInteractiveModeAsync(
        consoleService,
        interactiveService,
        versionResolver,
        configService,
        backupManager);
}

// Parse command-line arguments
return await Parser.Default.ParseArguments<Options>(args)
    .MapResult(
        async options =>
        {
            // Merge config file with CLI args (CLI args take precedence)
            await MergeConfigWithCliArgsAsync(options, args, configService);

            // Route to appropriate command handler
            return await CommandRouter.RouteCommand(
                options,
                consoleService,
                interactiveService,
                versionResolver,
                configService,
                backupManager);
        },
        _ => Task.FromResult(ExitCodes.ValidationError));

// Loads config file and merges with CLI arguments (CLI args take precedence).
static async Task MergeConfigWithCliArgsAsync(Options options, string[] args, ConfigService configService)
{
    var startDir = DetermineStartDirectory(options);
    var config = configService.LoadConfig(startDir);

    if (config != null)
    {
        var cliArgsProvided = CliArgumentParser.GetExplicitArguments(args);
        configService.MergeConfig(options, config, cliArgsProvided);
    }

    await Task.CompletedTask;
}

// Determines the starting directory from options.
static string DetermineStartDirectory(Options options)
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
