using System.Diagnostics;
using CommandLine;
using CPMigrate.Models;
using CPMigrate.Services;
using Microsoft.Extensions.Logging;

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
            return await CommandRouter.RouteCommand(
                new Options { Interactive = true },
                bootstrapServices
            );
        }

        if (CliVerbGuard.RejectsLeadingVerb(args, bootstrapServices.ConsoleService))
        {
            return ExitCodes.ValidationError;
        }

        // Parse command-line arguments
        return await Parser
            .Default.ParseArguments<Options>(args)
            .MapResult(
                async options =>
                {
                    using var loggerFactory = LoggingConfiguration.CreateLoggerFactory(
                        options.Verbose
                    );
                    var services = ApplicationServices.Create(customConsole, loggerFactory);

                    // Emitted before the config file is even read. `--completions zsh > _cpmigrate`
                    // is a documented redirection, and a "Loaded config from: …" notice would land
                    // inside the script — while a configured output format could make the reporting
                    // contract reject the command outright.
                    if (options.Completions.HasValue)
                    {
                        Console.WriteLine(
                            CompletionScriptGenerator.Generate(options.Completions.Value)
                        );
                        return ExitCodes.Success;
                    }

                    if (options.Explain is not null)
                    {
                        var (explanation, found) = RuleExplainer.Explain(options.Explain);
                        Console.WriteLine(explanation);
                        return found ? ExitCodes.Success : ExitCodes.ValidationError;
                    }

                    if (options.Doctor)
                    {
                        var doctorService = new DoctorService(
                            services.ConsoleService,
                            new SolutionDiscovery(services.ConsoleService));
                        return await doctorService.RunAsync(options.GetDiscoveryTargetPath());
                    }

                    if (options.Init)
                    {
                        var initService = new InitService(services.ConsoleService);
                        return await initService.RunAsync(
                            options.GetDiscoveryTargetPath(),
                            options.Force);
                    }

                    // Merge config file with CLI args (CLI args take precedence)
                    MergeConfigWithCliArgs(
                        options,
                        args,
                        services.ConfigService,
                        services.ConsoleService
                    );

                    WarnAboutIneffectiveFailOn(options, args, services.ConsoleService);

                    // Initialize logging based on --verbose flag. The notice is written before the
                    // payload, so under a machine-readable format it would put prose ahead of the
                    // opening brace and stop the document parsing at all.
                    if (options.Verbose && !options.Output.IsMachineReadable())
                    {
                        var logPath = Path.Combine(
                            Directory.GetCurrentDirectory(),
                            "cpmigrate.log"
                        );
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
                    if (
                        errors.Any(e =>
                            e.Tag == ErrorType.HelpRequestedError
                            || e.Tag == ErrorType.VersionRequestedError
                        )
                    )
                    {
                        return Task.FromResult(0);
                    }
                    return Task.FromResult(ExitCodes.ValidationError);
                }
            );
    }

    /// <summary>
    /// Warns when <c>--fail-on</c> was passed on the command line for a command it cannot affect.
    /// It only changes analysis exit codes, so without <c>--analyze</c> the default action — a real,
    /// file-rewriting migration — runs while the flag does nothing.
    ///
    /// A warning rather than an error, because the same setting can arrive from
    /// <c>.cpmigrate.json</c> as a team-wide policy; rejecting it would break every migration run
    /// in a repository that configures a gate. Only an explicit CLI flag is reported, since that is
    /// the case where the user was expecting it to apply to <em>this</em> command.
    /// </summary>
    private static void WarnAboutIneffectiveFailOn(
        Options options,
        string[] args,
        IConsoleService consoleService
    )
    {
        if (options.Analyze || options.Output.IsMachineReadable())
        {
            return;
        }

        if (!CliArgumentParser.GetExplicitArguments(args).Contains("fail-on"))
        {
            return;
        }

        consoleService.Warning(
            "--fail-on only affects --analyze; it is ignored for this command. "
                + "Did you mean to add --analyze?"
        );
    }

    /// <summary>
    /// Loads config file and merges with CLI arguments (CLI args take precedence).
    /// </summary>
    private static void MergeConfigWithCliArgs(
        Options options,
        string[] args,
        ConfigService configService,
        IConsoleService consoleService
    )
    {
        var startDir = options.GetConfigSearchStartDirectory();
        var (config, configPath, errorMessage) = configService.LoadConfigDetailed(startDir);

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            if (!options.Output.IsMachineReadable())
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

        if (!options.Output.IsMachineReadable() && !string.IsNullOrWhiteSpace(configPath))
        {
            consoleService.Dim($"Loaded config from: {configPath}");
        }
    }
}
