using CommandLine;
using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate;

/// <summary>
/// Entry point runner for the application, extracted for testability.
/// </summary>
public static class ProgramRunner
{
    /// <summary>
    /// Runs the application with the specified arguments.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, IConsoleService? customConsole = null)
    {
        // Setup composition root
        var versionResolver = new VersionResolver(null);
        var consoleService = customConsole ?? new SpectreConsoleService(versionResolver);
        var interactiveService = new InteractiveService(consoleService);
        var configService = new ConfigService(consoleService);
        var backupManager = new BackupManager();

        // Check for interactive mode (no args)
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
                errors =>
                {
                    if (errors.Any(e => e.Tag == ErrorType.HelpRequestedError || e.Tag == ErrorType.VersionRequestedError))
                    {
                        return Task.FromResult(0);
                    }
                    return Task.FromResult(ExitCodes.ValidationError);
                });
    }

    /// <summary>
    /// Loads config file and merges with CLI arguments (CLI args take precedence).
    /// </summary>
    private static async Task MergeConfigWithCliArgsAsync(Options options, string[] args, ConfigService configService)
    {
        var startDir = DetermineStartDirectory(options);
        var config = configService.LoadConfig(startDir);

        if (config != null)
        {
            var cliArgsProvided = CliArgumentParser.GetExplicitArguments(args);
            ConfigService.MergeConfig(options, config, cliArgsProvided);
        }

        await Task.CompletedTask;
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
}
