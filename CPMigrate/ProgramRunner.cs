using CommandLine;
using CPMigrate.Models;
using CPMigrate.Services;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
        var bootstrapServices = ApplicationServices.Create(customConsole);

        // Check for interactive mode (no args)
        if (args.Length == 0)
        {
            return await CommandRouter.RouteCommand(new Options { Interactive = true }, bootstrapServices);
        }

        // Parse command-line arguments
        return await Parser.Default.ParseArguments<Options>(args)
            .MapResult(
                async options =>
                {
                    using var loggerFactory = LoggingConfiguration.CreateLoggerFactory(options.Verbose);
                    var services = ApplicationServices.Create(customConsole, loggerFactory);

                    // Merge config file with CLI args (CLI args take precedence)
                    MergeConfigWithCliArgs(options, args, services.ConfigService, services.ConsoleService);

                    // Initialize logging based on --verbose flag
                    if (options.Verbose)
                    {
                        var logPath = Path.Combine(Directory.GetCurrentDirectory(), "cpmigrate.log");
                        services.ConsoleService.Dim($"Verbose logging enabled: {logPath}");
                    }
                    var logger = loggerFactory.CreateLogger("CPMigrate");
                    logger.LogDebug("CPMigrate started with args: {Args}", string.Join(" ", args));

                    // Route to appropriate command handler
                    var stopwatch = Stopwatch.StartNew();
                    var exitCode = await CommandRouter.RouteCommand(options, services);
                    stopwatch.Stop();

                    TelemetryService.RecordCommandRun(options, exitCode, stopwatch.Elapsed);
                    return exitCode;
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
    private static void MergeConfigWithCliArgs(
        Options options,
        string[] args,
        ConfigService configService,
        IConsoleService consoleService)
    {
        var startDir = options.GetConfigSearchStartDirectory();
        var (config, configPath, errorMessage) = configService.LoadConfigDetailed(startDir);

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            if (options.Output != OutputFormat.Json)
            {
                consoleService.Warning(errorMessage);
            }

            return;
        }

        if (config == null)
        {
            return;
        }

        var cliArgsProvided = CliArgumentParser.GetExplicitArguments(args);
        ConfigService.MergeConfig(options, config, cliArgsProvided);

        if (options.Output != OutputFormat.Json && !string.IsNullOrWhiteSpace(configPath))
        {
            consoleService.Dim($"Loaded config from: {configPath}");
        }
    }
}
