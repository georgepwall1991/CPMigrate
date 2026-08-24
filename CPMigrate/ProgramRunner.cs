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

                    if (RejectsValuelessWhy(options, args, services.ConsoleService))
                    {
                        return ExitCodes.ValidationError;
                    }

                    // Ahead of the modes below, which return before CommandRouter ever validates.
                    // `--init --rules NoSuchRule=none` otherwise wrote a config file and exited
                    // successfully, so the promised strict rejection depended on which command the
                    // policy happened to be passed to.
                    NormalizeValuelessRuleFlag(options, args);
                    if (
                        IsDiagnosticMode(options)
                        && (
                            RejectsUnusableRulePolicy(options, services.ConsoleService)
                            || RejectsUnusableVerifyRequest(options, services.ConsoleService)
                        )
                    )
                    {
                        return ExitCodes.ValidationError;
                    }

                    // Ahead of the diagnostic modes, which return without reaching the router: a
                    // policy those commands cannot use is still a policy the caller expected to
                    // apply, and silence is what makes that invisible.
                    WarnAboutIneffectiveAnalysisFlag(
                        options,
                        args,
                        "fail-on",
                        services.ConsoleService
                    );
                    WarnAboutIneffectiveAnalysisFlag(
                        options,
                        args,
                        "rules",
                        services.ConsoleService
                    );

                    if (options.Doctor)
                    {
                        var doctorService = new DoctorService(
                            services.ConsoleService,
                            new SolutionDiscovery(services.ConsoleService)
                        );
                        return await doctorService.RunAsync(options.GetDiscoveryTargetPath());
                    }

                    if (options.Init)
                    {
                        var initService = new InitService(services.ConsoleService);
                        return await initService.RunAsync(
                            options.GetDiscoveryTargetPath(),
                            options.Force
                        );
                    }

                    if (options.Status)
                    {
                        var statusService = new StatusService(
                            services.ConsoleService,
                            new SolutionDiscovery(services.ConsoleService)
                        );
                        return await statusService.RunAsync(options.GetDiscoveryTargetPath());
                    }

                    if (options.Tree)
                    {
                        return await RunTreeModeAsync(options, services);
                    }

                    if (options.Why is not null)
                    {
                        return await RunWhyModeAsync(options, services);
                    }

                    // Unconditionally, and not inside the merge: that returns early when no config
                    // file is found, which is the common case — so a valueless --rules would be
                    // rejected only in repositories that happen to have a .cpmigrate.json.
                    NormalizeValuelessRuleFlag(options, args);

                    // Merge config file with CLI args (CLI args take precedence)
                    MergeConfigWithCliArgs(
                        options,
                        args,
                        services.ConfigService,
                        services.ConsoleService
                    );

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

    private static async Task<int> RunTreeModeAsync(Options options, ApplicationServices services)
    {
        try
        {
            var projectAnalyzer = services.ProjectAnalyzer;
            var targetPath = options.GetDiscoveryTargetPath();
            var (basePath, projectPaths) = await projectAnalyzer.DiscoverProjectsFromSolutionAsync(
                targetPath
            );

            var allReferences = new List<Models.PackageReference>();
            foreach (var projectPath in projectPaths)
            {
                var (references, success) = await projectAnalyzer.ScanResolvedPackagesAsync(
                    projectPath,
                    options.IncludeTransitive
                );
                if (success)
                {
                    allReferences.AddRange(references);
                }
            }

            var packageInfo = new Models.ProjectPackageInfo(allReferences, BasePath: basePath);
            var treeService = new DependencyTreeService(services.ConsoleService);
            return await treeService.RunAsync(packageInfo);
        }
        catch (Exception ex)
        {
            services.ConsoleService.Error($"Failed to build dependency tree: {ex.Message}");
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Traces one package through the workspace: who declares it, who only sees it transitively,
    /// and whether the versions agree. Mirrors <see cref="RunTreeModeAsync"/>: a diagnostic mode
    /// that scans every project itself, renders to the terminal, and reports an incomplete scan
    /// rather than dressing partial data up as a complete answer.
    /// </summary>
    private static async Task<int> RunWhyModeAsync(Options options, ApplicationServices services)
    {
        try
        {
            var projectAnalyzer = services.ProjectAnalyzer;
            var targetPath = options.GetDiscoveryTargetPath();
            var (basePath, projectPaths) = await projectAnalyzer.DiscoverProjectsFromSolutionAsync(
                targetPath
            );

            var allReferences = new List<Models.PackageReference>();
            var declaredReferences = new List<Models.PackageReference>();
            var resolvedGraphs = new List<Models.ProjectResolvedGraph>();
            var graphService = new DependencyGraphService(services.ConsoleService);
            var failedScans = 0;
            var scanOutcomes = new List<PackageOriginProjectScan>();
            foreach (var projectPath in projectPaths)
            {
                var (references, success) = await projectAnalyzer.ScanResolvedPackagesAsync(
                    projectPath,
                    includeTransitive: true
                );
                var (declarations, declarationsRead) = projectAnalyzer.ScanDeclaredPackages(
                    projectPath
                );
                if (!success || !declarationsRead)
                {
                    failedScans++;
                }
                scanOutcomes.Add(
                    new PackageOriginProjectScan(
                        projectPath,
                        ResolvedRead: success,
                        DeclarationsRead: declarationsRead
                    )
                );
                if (success)
                {
                    allReferences.AddRange(references);
                }
                if (declarationsRead)
                {
                    declaredReferences.AddRange(declarations);
                }

                // The resolved graph carries the edges a flat package list lacks: without it a
                // transitive sighting cannot name the direct package that pulled it in.
                var graph = graphService.TryReadResolvedGraph(projectPath);
                if (graph is not null)
                {
                    resolvedGraphs.Add(graph);
                }
            }

            var packageInfo = new Models.ProjectPackageInfo(
                allReferences,
                BasePath: basePath,
                DeclaredReferences: declaredReferences
            );
            var whyService = new PackageOriginService(services.ConsoleService);
            return await whyService.RunAsync(
                new PackageOriginRequest(
                    options.Why!,
                    packageInfo,
                    resolvedGraphs,
                    projectPaths.Count,
                    failedScans,
                    // Every discovered project, including ones whose scans produced no rows —
                    // "this project does not have the package" is part of the answer.
                    projectPaths,
                    scanOutcomes
                )
            );
        }
        catch (Exception ex)
        {
            services.ConsoleService.Error($"Failed to trace package origin: {ex.Message}");
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Reports a valueless <c>--why</c> and says the run should stop.
    ///
    /// A valueless <c>--why</c> parses as "flag not set", and an unset <c>--why</c> means the
    /// default action — a real, file-rewriting migration. Someone who typed <c>cpmigrate --why</c>
    /// must be told the package ID is missing, never migrated.
    /// </summary>
    private static bool RejectsValuelessWhy(
        Options options,
        string[] args,
        IConsoleService consoleService
    )
    {
        if (
            !CliArgumentParser.GetExplicitArguments(args).Contains("why")
            || !string.IsNullOrWhiteSpace(options.Why)
        )
        {
            return false;
        }

        consoleService.Error("--why requires a package ID, e.g. --why Newtonsoft.Json.");
        return true;
    }

    /// <summary>
    /// Warns when a flag that only shapes analysis output was passed on the command line for a
    /// command it cannot affect. Without <c>--analyze</c> the default action — a real,
    /// file-rewriting migration — runs while the flag does nothing.
    ///
    /// A warning rather than an error, because the same settings can arrive from
    /// <c>.cpmigrate.json</c> as team-wide policy; rejecting them would break every migration run
    /// in a repository that configures a gate. Only an explicit CLI flag is reported, since that is
    /// the case where the user was expecting it to apply to <em>this</em> command.
    /// </summary>
    /// <param name="options">The resolved options.</param>
    /// <param name="args">The raw command line, used to tell a flag from a configured default.</param>
    /// <param name="flag">The long option name, without leading dashes.</param>
    /// <param name="consoleService">Where the warning goes.</param>
    private static void WarnAboutIneffectiveAnalysisFlag(
        Options options,
        string[] args,
        string flag,
        IConsoleService consoleService
    )
    {
        // Asking the router rather than reading options.Analyze: the flag is not the dispatch
        // decision. `--update --analyze` performs the update, and `--init` returns long before any
        // analysis — in both cases the policy is ignored, which is exactly when the warning is
        // owed. Diagnostic modes are this file's own, so they are excluded here.
        if (!IsDiagnosticMode(options) && CommandRouter.PerformsAnalysis(options))
        {
            return;
        }

        // Deliberately not skipped under a machine-readable format. Warnings go to stderr now, so
        // they cannot corrupt the payload — and suppressing this one is precisely how a CI job
        // never learns that the policy it passed was ignored.
        if (!CliArgumentParser.GetExplicitArguments(args).Contains(flag))
        {
            return;
        }

        consoleService.Warning(
            $"--{flag} only affects --analyze; it is ignored for this command. "
                + "Did you mean to add --analyze?"
        );
    }

    /// <summary>
    /// Turns a valueless <c>--rules</c> into an empty spec so validation rejects it.
    ///
    /// <para>
    /// A trailing <c>--rules</c> leaves the property null, which is indistinguishable from the flag
    /// never being passed — so the run proceeds with no policy at all. That is worse than it looks:
    /// the argument still counts as explicit, so it also suppresses the <c>rules</c> map from
    /// <c>.cpmigrate.json</c>, and a team's configured gate silently moves. Marking it empty routes
    /// it into the same rejection as <c>--rules ""</c>, which reports the reason and, under a
    /// machine-readable format, emits it as a payload.
    /// </para>
    /// </summary>
    /// <summary>
    /// The modes handled here rather than by <c>CommandRouter</c>. They return before the router
    /// validates anything, and they report to the terminal rather than emitting a machine-readable
    /// payload — which is why they get their own rule-policy check, and why every other mode is
    /// deliberately left to the router, where a rejection also reaches a JSON or SARIF consumer.
    /// </summary>
    private static bool IsDiagnosticMode(Options options)
    {
        return options.Doctor || options.Init || options.Status || options.Tree || options.Why is not null;
    }

    /// <summary>
    /// Reports an unusable <c>--rules</c> policy and says the run should stop. Applied before the
    /// diagnostic modes, which return without ever reaching <c>CommandRouter</c>'s validation — so
    /// without this, whether a typo was caught depended on which command it was passed to.
    /// </summary>
    /// <returns>True when the policy cannot be understood and the run must not proceed.</returns>
    private static bool RejectsUnusableRulePolicy(Options options, IConsoleService consoleService)
    {
        try
        {
            options.ValidateRuleOptions();
            return false;
        }
        catch (ArgumentException ex)
        {
            consoleService.Error(ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Reports a <c>--verify</c> that this command cannot honour and says the run should stop.
    /// </summary>
    /// <remarks>
    /// Same reason as the rule-policy check above, and a worse consequence. The diagnostic modes
    /// return from here without reaching <c>CommandRouter</c>, so a rejection enforced only
    /// downstream let <c>--verify --init</c> write a config file and exit <c>0</c> — a success from a
    /// command that was asked to prove something and proved nothing. For a flag whose entire purpose
    /// is to refuse to report an unmeasured run as clean, being silently dropped is the one failure
    /// it must not have.
    /// </remarks>
    /// <returns>True when the combination is unsupported and the run must not proceed.</returns>
    private static bool RejectsUnusableVerifyRequest(
        Options options,
        IConsoleService consoleService
    )
    {
        try
        {
            options.ValidateVerifyOptions();
            return false;
        }
        catch (ArgumentException ex)
        {
            consoleService.Error(ex.Message);
            return true;
        }
    }

    private static void NormalizeValuelessRuleFlag(Options options, string[] args)
    {
        if (options.Rules is null && CliArgumentParser.GetExplicitArguments(args).Contains("rules"))
        {
            options.Rules = string.Empty;
        }
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
        var (config, configPath, errorMessage, warnings) = configService.LoadConfigDetailed(
            startDir
        );

        // ErrorMessage is a fatal load failure (config == null). Warning-only findings —
        // contradictory settings, unknown keys — ride the warnings list; they never disable
        // merging, because valid settings in the file must stay active either way.
        if (!options.Output.IsMachineReadable())
        {
            foreach (var warning in warnings)
            {
                consoleService.Warning(warning);
            }
        }

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
