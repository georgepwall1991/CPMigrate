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
                        return await RunExplainModeAsync(options, services.ConsoleService);
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
                        return await RunDoctorModeAsync(options, services);
                    }

                    if (options.Init)
                    {
                        // Under --output Json the stdout contract is one parseable document, so
                        // the service's own narration must not leak into it — same swap
                        // CommandRouter makes for its own machine-readable modes.
                        var initConsole =
                            options.Output == OutputFormat.Json
                                ? SilentConsoleService.Instance
                                : services.ConsoleService;
                        var initService = new InitService(initConsole);
                        return await initService.RunAsync(
                            options.GetDiscoveryTargetPath(),
                            options.Force,
                            options
                        );
                    }

                    if (options.Status)
                    {
                        return await RunStatusModeAsync(options, services);
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

    /// <summary>
    /// Renders the rule explanation. Under <c>--output Json</c> the same catalog entry is emitted
    /// as the <c>explain</c> document instead — one parseable payload on stdout, so an IDE
    /// extension or CI script can read rule metadata without parsing prose.
    /// </summary>
    private static async Task<int> RunExplainModeAsync(Options options, IConsoleService console)
    {
        if (options.Output == OutputFormat.Json)
        {
            var (rule, allRules, suggestions) = RuleExplainer.Resolve(options.Explain!);
            var found = rule is not null || allRules is not null;
            await JsonOutputWriter.EmitAsync(
                ExplainJsonWriter.Serialize(
                    options.Explain!,
                    rule,
                    allRules,
                    suggestions,
                    found ? ExitCodes.Success : ExitCodes.ValidationError
                ),
                options,
                console
            );
            return found ? ExitCodes.Success : ExitCodes.ValidationError;
        }

        var (explanation, foundText) = RuleExplainer.Explain(options.Explain!);
        Console.WriteLine(explanation);
        return foundText ? ExitCodes.Success : ExitCodes.ValidationError;
    }

    /// <summary>
    /// Renders the workspace dashboard. Under <c>--output Json</c> the same collected status is
    /// emitted as the <c>status</c> document instead — one parseable payload on stdout. The other
    /// machine-readable formats are rejected: they carry analyzer findings, and a status has none.
    /// </summary>
    /// <summary>
    /// Runs the environment checks. Under <c>--output Json</c> the same check list is emitted as
    /// the <c>doctor</c> document instead — one parseable payload on stdout, so a CI gate can
    /// read which check failed rather than only the exit code. The other machine-readable
    /// formats are rejected: they carry analyzer findings, and doctor has none.
    /// </summary>
    private static async Task<int> RunDoctorModeAsync(Options options, ApplicationServices services)
    {
        // SARIF, Markdown, and CSV all carry analyzer findings, and --doctor produces none —
        // running the checks anyway would print a table after a caller explicitly asked for a
        // document.
        if (options.Output is OutputFormat.Sarif or OutputFormat.Markdown or OutputFormat.Csv)
        {
            services.ConsoleService.Error(
                $"--output {options.Output} cannot be combined with --doctor; that format reports "
                    + "analyzer findings only."
            );
            return ExitCodes.ValidationError;
        }

        // Under --output Json the stdout contract is one parseable document, so the service's own
        // narration must not leak into it — same swap CommandRouter makes for its own
        // machine-readable modes.
        var userConsole = services.ConsoleService;
        var executionConsole =
            options.Output == OutputFormat.Json
                ? SilentConsoleService.Instance
                : services.ConsoleService;

        var doctorService = new DoctorService(
            executionConsole,
            new SolutionDiscovery(executionConsole)
        );

        try
        {
            return await doctorService.RunAsync(
                options.GetDiscoveryTargetPath(),
                options,
                options.BackupDir
            );
        }
        catch (Exception ex)
        {
            // A failed --output-file write lands here: report it as a failure payload rather than
            // aborting with no document at all — same contract --tree and --status keep.
            return await EmitDoctorFailureAsync(
                options,
                ExitCodes.UnexpectedError,
                $"Failed to run environment checks: {ex.Message}",
                userConsole
            );
        }
    }

    /// <summary>
    /// Reports a <c>--doctor</c> run that cannot produce a document and settles its exit code.
    /// Mirrors <see cref="EmitStatusFailureAsync"/>: under <c>--output Json</c> the prose goes to
    /// the caller's own console and the standard failure payload goes out through
    /// <see cref="JsonOutputWriter.EmitFailureAsync"/>.
    /// </summary>
    private static async Task<int> EmitDoctorFailureAsync(
        Options options,
        int exitCode,
        string errorMessage,
        IConsoleService consoleService
    )
    {
        consoleService.Error(errorMessage);

        if (options.Output == OutputFormat.Json)
        {
            var formatter = new JsonFormatter();
            var operationResult = new OperationResult
            {
                Operation = "doctor",
                Success = false,
                ExitCode = exitCode,
                Errors = [errorMessage],
            };
            await JsonOutputWriter.EmitFailureAsync(formatter.Format(operationResult), options);
        }

        return exitCode;
    }

    /// <summary>
    /// Renders the workspace dashboard. Under <c>--output Json</c> the same collected status is
    /// emitted as the <c>status</c> document instead — one parseable payload on stdout. The other
    /// machine-readable formats are rejected: they carry analyzer findings, and a status has none.
    /// </summary>
    private static async Task<int> RunStatusModeAsync(Options options, ApplicationServices services)
    {
        // SARIF, Markdown, and CSV all carry analyzer findings, and --status produces none —
        // collecting anyway would print a dashboard after a caller explicitly asked for a document.
        if (options.Output is OutputFormat.Sarif or OutputFormat.Markdown or OutputFormat.Csv)
        {
            services.ConsoleService.Error(
                $"--output {options.Output} cannot be combined with --status; that format reports "
                    + "analyzer findings only."
            );
            return ExitCodes.ValidationError;
        }

        // Under --output Json the stdout contract is one parseable document, so the service's own
        // narration must not leak into it — same swap CommandRouter makes for its own
        // machine-readable modes.
        var userConsole = services.ConsoleService;
        var executionConsole =
            options.Output == OutputFormat.Json
                ? SilentConsoleService.Instance
                : services.ConsoleService;

        var statusService = new StatusService(
            executionConsole,
            new SolutionDiscovery(executionConsole)
        );

        try
        {
            return await statusService.RunAsync(options.GetDiscoveryTargetPath(), options);
        }
        catch (Exception ex)
        {
            // A failed --output-file write lands here: report it as a failure payload rather than
            // aborting with no document at all — same contract --tree keeps.
            return await EmitStatusFailureAsync(
                options,
                ExitCodes.UnexpectedError,
                $"Failed to collect workspace status: {ex.Message}",
                userConsole
            );
        }
    }

    /// <summary>
    /// Reports a <c>--status</c> run that cannot produce a document and settles its exit code.
    /// Mirrors <see cref="EmitTreeFailureAsync"/>: under <c>--output Json</c> the prose goes to the
    /// caller's own console and the standard failure payload goes out through
    /// <see cref="JsonOutputWriter.EmitFailureAsync"/>.
    /// </summary>
    private static async Task<int> EmitStatusFailureAsync(
        Options options,
        int exitCode,
        string errorMessage,
        IConsoleService consoleService
    )
    {
        consoleService.Error(errorMessage);

        if (options.Output == OutputFormat.Json)
        {
            var formatter = new JsonFormatter();
            var operationResult = new OperationResult
            {
                Operation = "status",
                Success = false,
                ExitCode = exitCode,
                Errors = [errorMessage],
            };
            await JsonOutputWriter.EmitFailureAsync(formatter.Format(operationResult), options);
        }

        return exitCode;
    }

    /// <summary>
    /// Renders the dependency tree. Under <c>--output Json</c> the same scan is emitted as the
    /// <c>tree</c> document instead — one parseable payload on stdout, with every discovered
    /// project present so an unread one is never mistaken for an empty one. The other
    /// machine-readable formats are rejected: they carry analyzer findings, and a tree has none.
    /// </summary>
    private static async Task<int> RunTreeModeAsync(Options options, ApplicationServices services)
    {
        // SARIF, Markdown, and CSV all carry analyzer findings, and --tree produces none — running
        // the scan anyway would emit a console tree after a caller explicitly asked for a document.
        if (options.Output is OutputFormat.Sarif or OutputFormat.Markdown or OutputFormat.Csv)
        {
            services.ConsoleService.Error(
                $"--output {options.Output} cannot be combined with --tree; that format reports "
                    + "analyzer findings only."
            );
            return ExitCodes.ValidationError;
        }

        var userConsole = services.ConsoleService;
        var executionConsole =
            options.Output == OutputFormat.Json
                ? SilentConsoleService.Instance
                : services.ConsoleService;

        // Under --output Json the stdout contract is one parseable document, so discovery notices
        // ("Found project: …") must not leak into it — same swap CommandRouter makes for its own
        // machine-readable modes.
        if (!ReferenceEquals(executionConsole, services.ConsoleService))
        {
            services = services.WithConsole(executionConsole);
        }

        try
        {
            var projectAnalyzer = services.ProjectAnalyzer;
            var targetPath = options.GetDiscoveryTargetPath();
            var discovery = await projectAnalyzer.DiscoverProjectsDetailedAsync(targetPath);
            var (basePath, projectPaths) = (discovery.BasePath, discovery.ProjectPaths);

            // Projects the solution names but the filesystem does not have. Under a
            // machine-readable format the discovery console was silenced, so the warning it
            // emitted went nowhere — replay it on the caller's console (stderr) and carry the
            // projects into the document as unread, or the run reports a complete scan over a
            // workspace that is not.
            if (discovery.MissingProjects.Count > 0 && options.Output == OutputFormat.Json)
            {
                foreach (var missing in discovery.MissingProjects)
                {
                    userConsole.Warning(
                        $"Project found in solution but file missing: {missing}"
                    );
                }
            }

            // Discovery found nothing — no solution where one was asked for, or one it could not
            // read. The terminal path has already said so in prose; the JSON path must not dress
            // that up as an empty workspace, so it emits the standard failure payload instead.
            if (projectPaths.Count == 0 && discovery.MissingProjects.Count == 0 && options.Output == OutputFormat.Json)
            {
                return await EmitTreeFailureAsync(
                    options,
                    ExitCodes.NoProjectsFound,
                    $"No projects discovered at '{targetPath}'; nothing was scanned.",
                    userConsole
                );
            }

            // The restores run concurrently, scheduled by the shared scan pattern (see
            // ScanResolvedPackagesConcurrentlyAsync below); the merge afterwards walks discovery
            // order — replaying each project's warnings where the sequential loop would have
            // printed them — so the tree cannot depend on completion order.
            var resolved = await ScanResolvedPackagesConcurrentlyAsync(
                options,
                services,
                projectPaths,
                includeTransitive: options.IncludeTransitive
            );
            var allReferences = new List<Models.PackageReference>();
            var projectOutcomes = new List<(string Path, bool Scanned)>(projectPaths.Count);
            for (var index = 0; index < projectPaths.Count; index++)
            {
                // Warnings go to the caller's console (stderr), not the execution console: under
                // --output Json the execution console is silent, and a dropped warning is how a
                // scanned:false project ends up unexplained.
                foreach (var warning in resolved[index].Warnings)
                {
                    userConsole.Warning(warning);
                }

                projectOutcomes.Add((projectPaths[index], resolved[index].Success));
                if (resolved[index].Success)
                {
                    allReferences.AddRange(resolved[index].References);
                }
            }

            // A project the solution names but the filesystem lacks was never scanned — it belongs
            // in the document as unread, not absent.
            foreach (var missing in discovery.MissingProjects)
            {
                projectOutcomes.Add((missing, false));
            }

            var packageInfo = new Models.ProjectPackageInfo(allReferences, BasePath: basePath);

            if (options.Output == OutputFormat.Json)
            {
                // EmitAsync, not EmitFailureAsync: a success document that could not reach its
                // --output-file must not fall back to stdout and exit 0 — a CI job expecting the
                // file would pass with a missing artifact. The write failure throws into the catch
                // below, which reports it as a failure payload instead.
                await JsonOutputWriter.EmitAsync(
                    DependencyTreeJsonWriter.Serialize(
                        packageInfo,
                        projectOutcomes,
                        ExitCodes.Success
                    ),
                    options,
                    userConsole
                );
                return ExitCodes.Success;
            }
            var treeService = new DependencyTreeService(executionConsole);
            return await treeService.RunAsync(packageInfo);
        }
        catch (Exception ex)
        {
            return await EmitTreeFailureAsync(
                options,
                ExitCodes.UnexpectedError,
                $"Failed to build dependency tree: {ex.Message}",
                userConsole
            );
        }
    }

    /// <summary>
    /// Reports a <c>--tree</c> run that cannot produce a document and settles its exit code.
    /// Mirrors <see cref="EmitWhyFailureAsync"/>: under <c>--output Json</c> the prose goes to the
    /// caller's own console and the standard failure payload goes out through
    /// <see cref="JsonOutputWriter.EmitFailureAsync"/>.
    /// </summary>
    private static async Task<int> EmitTreeFailureAsync(
        Options options,
        int exitCode,
        string errorMessage,
        IConsoleService consoleService
    )
    {
        consoleService.Error(errorMessage);

        if (options.Output == OutputFormat.Json)
        {
            var formatter = new JsonFormatter();
            var operationResult = new OperationResult
            {
                Operation = "tree",
                Success = false,
                ExitCode = exitCode,
                Errors = [errorMessage],
            };
            await JsonOutputWriter.EmitFailureAsync(formatter.Format(operationResult), options);
        }

        return exitCode;
    }

    /// <summary>
    /// Traces one or more comma-separated packages through the workspace — one restore-backed
    /// scan, then one answer per ID over the shared data: who declares each package, who only sees
    /// it transitively, and whether the versions agree. Mirrors <see cref="RunTreeModeAsync"/>: a
    /// diagnostic mode that scans every project itself and reports an incomplete scan rather than
    /// dressing partial data up as a complete answer.
    ///
    /// <para>
    /// With <c>--output Json</c> the answer is one JSON document on stdout instead of console
    /// rendering: a single-package run emits the published whyReport document, and several IDs
    /// emit the multi-package document under its own <c>why-many</c> operation — same analysis,
    /// same exit codes as the terminal path. <c>--output Sarif</c> is rejected outright, because
    /// SARIF reports analyzer findings and --why produces none.
    /// </para>
    /// </summary>
    private static async Task<int> RunWhyModeAsync(Options options, ApplicationServices services)
    {
        // SARIF carries analyzer findings, and --why produces none — running the scan anyway would
        // emit a terminal tree after a caller explicitly asked for a SARIF document. Rejected here
        // rather than left to Options.Validate, which this mode returns before ever reaching.
        if (options.Output == OutputFormat.Sarif)
        {
            services.ConsoleService.Error(
                "--output Sarif cannot be combined with --why; SARIF reports analyzer findings "
                    + "only."
            );
            return ExitCodes.ValidationError;
        }

        // --why takes one or more comma-separated package IDs; every answer is served by a single
        // workspace scan below. Parsing happens before anything touches the disk so a malformed
        // list fails in milliseconds instead of after a full restore-backed scan.
        IReadOnlyList<string> packageIds;

        try
        {
            packageIds = SplitWhyPackageIds(options.Why!);
        }
        catch (ArgumentException rejection)
        {
            services.ConsoleService.Error(rejection.Message);
            return ExitCodes.ValidationError;
        }

        if (packageIds.Count == 0)
        {
            services.ConsoleService.Error(
                "--why requires a package ID, e.g. --why Newtonsoft.Json or "
                    + "--why Newtonsoft.Json,Serilog."
            );
            return ExitCodes.ValidationError;
        }

        var userConsole = services.ConsoleService;
        var executionConsole =
            options.Output == OutputFormat.Json
                ? SilentConsoleService.Instance
                : services.ConsoleService;

        // Under --output Json the stdout contract is one parseable document, so discovery notices
        // ("Found project: …") must not leak into it — same swap CommandRouter makes for its own
        // machine-readable modes.
        if (!ReferenceEquals(executionConsole, services.ConsoleService))
        {
            services = services.WithConsole(executionConsole);
        }

        try
        {
            var projectAnalyzer = services.ProjectAnalyzer;
            var targetPath = options.GetDiscoveryTargetPath();
            var discovery = await projectAnalyzer.DiscoverProjectsDetailedAsync(targetPath);
            var (basePath, projectPaths) = (discovery.BasePath, discovery.ProjectPaths);

            // Projects the solution names but the filesystem does not have. Under a
            // machine-readable format the discovery console was silenced, so the warning it
            // emitted went nowhere — replay it on the caller's console (stderr) and carry the
            // projects into the report as unread, or the run reports a complete scan over a
            // workspace that is not.
            if (discovery.MissingProjects.Count > 0 && options.Output == OutputFormat.Json)
            {
                foreach (var missing in discovery.MissingProjects)
                {
                    userConsole.Warning(
                        $"Project found in solution but file missing: {missing}"
                    );
                }
            }

            // Discovery found nothing — no solution where one was asked for, or one it could not
            // read. The terminal path has already said so in prose; the JSON path must not dress
            // that up as a not-found verdict about the package, so it emits the router's standard
            // failure payload instead.
            if (projectPaths.Count == 0 && discovery.MissingProjects.Count == 0 && options.Output == OutputFormat.Json)
            {
                return await EmitWhyFailureAsync(
                    options,
                    ExitCodes.NoProjectsFound,
                    $"No projects discovered at '{targetPath}'; nothing was scanned.",
                    userConsole
                );
            }

            var graphService = new DependencyGraphService(executionConsole);
            // Phase one: the restore-backed resolved scans run concurrently, scheduled exactly as
            // the migration analysis schedules its own resolved pass — directory groups, the
            // process-wide redirect lock, the global concurrency gate (see
            // ScanResolvedPackagesConcurrentlyAsync below). The resolved graph is captured inside
            // that same locked window: projects sharing a directory overwrite each other's
            // obj/project.assets.json at every restore, so reading it afterwards would show only
            // the last project's edges.
            var resolved = await ScanResolvedPackagesConcurrentlyAsync(
                options,
                services,
                projectPaths,
                includeTransitive: true,
                graphService
            );

            // Phase two stays serial, and deliberately. ScanDeclaredPackages reads through MSBuild's
            // object model, whose static caches are not thread-safe — concurrent reads have produced
            // projects reporting each other's package versions, which is why the analysis pass keeps
            // this half serial too. Everything here aggregates in project order — replaying each
            // project's buffered scan warnings where the sequential loop would have printed them —
            // so failed-scan counts, ScanOutcomes alignment and every list are identical to what
            // the sequential loop produced, whatever order the restores finished in.
            var allReferences = new List<Models.PackageReference>();
            var declaredReferences = new List<Models.PackageReference>();
            var resolvedGraphs = new List<Models.ProjectResolvedGraph>();
            var failedScans = 0;
            var scanOutcomes = new List<PackageOriginProjectScan>();
            for (var index = 0; index < projectPaths.Count; index++)
            {
                var projectPath = projectPaths[index];
                // Warnings go to the caller's console (stderr), not the execution console: under
                // --output Json the execution console is silent, and a dropped warning is how an
                // unreadable project ends up unexplained.
                foreach (var warning in resolved[index].Warnings)
                {
                    userConsole.Warning(warning);
                }

                var (references, success) = (resolved[index].References, resolved[index].Success);
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

                // Captured during phase one while this project's restore was the freshest thing
                // in its obj directory — see the comment above.
                var graph = resolved[index].Graph;
                if (graph is not null)
                {
                    resolvedGraphs.Add(graph);
                }
            }

            // A project the solution names but the filesystem lacks was never scanned — it belongs
            // in the report as unreadable, not absent, and it counts toward the failed-scan total
            // that drives exit code 8.
            foreach (var missing in discovery.MissingProjects)
            {
                failedScans++;
                scanOutcomes.Add(
                    new PackageOriginProjectScan(
                        missing,
                        ResolvedRead: false,
                        DeclarationsRead: false
                    )
                );
            }

            var allProjectPaths = projectPaths.Concat(discovery.MissingProjects).ToList();
            var packageInfo = new Models.ProjectPackageInfo(
                allReferences,
                BasePath: basePath,
                DeclaredReferences: declaredReferences
            );
            var whyService = new PackageOriginService(executionConsole);
            // One request per asked-about package, all over the same scanned data: this is the
            // entire point of the comma-separated list — the restore-backed scan above runs once
            // no matter how many IDs a CI job is auditing.
            var requests = packageIds
                .Select(packageId => new PackageOriginRequest(
                    packageId,
                    packageInfo,
                    resolvedGraphs,
                    allProjectPaths.Count,
                    failedScans,
                    // Every discovered project, including ones whose scans produced no rows —
                    // "this project does not have the package" is part of the answer.
                    allProjectPaths,
                    scanOutcomes
                ))
                .ToList();
            // Machine-readable mode answers in one JSON document instead of console rendering.
            // Analysis and exit codes are shared with the console path; only the rendering differs:
            // one ID emits the single-package document, several the multi-package one — both
            // carrying exactly the verdicts and codes the terminal path settles.
            if (options.Output == OutputFormat.Json)
            {
                var answers = requests
                    .Select(request =>
                    {
                        var (report, answerExitCode) = PackageOriginService.AnalyzeQuietly(
                            request
                        );
                        return (request, report, answerExitCode);
                    })
                    .ToList();

                // The process verdict is the worst of the per-package answers, folded exactly as
                // the terminal multi-package path folds its own.
                var exitCode = answers.Count == 1
                    ? answers[0].answerExitCode
                    : PackageOriginService.CombineExitCodes(
                        [.. answers.Select(answer => answer.answerExitCode)]
                    );

                // EmitAsync, not EmitFailureAsync: a success document that could not reach its
                // --output-file must not fall back to stdout and exit 0 — a CI job expecting the
                // file would pass with a missing artifact. The write failure throws into the catch
                // below, which reports it as a failure payload instead.
                await JsonOutputWriter.EmitAsync(
                    answers.Count == 1
                        ? PackageOriginJsonWriter.Serialize(
                            answers[0].request,
                            answers[0].report,
                            answers[0].answerExitCode
                        )
                        : PackageOriginJsonWriter.SerializeMany(answers, exitCode),
                    options,
                    userConsole
                );

                return exitCode;
            }

            return requests.Count == 1
                ? await whyService.RunAsync(requests[0])
                : await whyService.RunManyAsync(requests);
        }
        catch (Exception ex)
        {
            return await EmitWhyFailureAsync(
                options,
                ExitCodes.UnexpectedError,
                $"Failed to trace package origin: {ex.Message}",
                userConsole
            );
        }
    }

    /// <summary>
    /// Splits a comma-separated <c>--why</c> value into package IDs, trimming whitespace around
    /// each and collapsing duplicates (case-insensitively — package IDs compare the same way
    /// everywhere else in the tool), first occurrence first.
    ///
    /// <para>
    /// A segment left empty between commas is reported rather than skipped: <c>--why
    /// Newtonsoft.Json,,Serilog</c> is almost certainly a typo, and silently answering two of the
    /// three questions a deny-list job asked would look exactly like a pass.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> SplitWhyPackageIds(string raw)
    {
        var segments = raw.Split(',').Select(id => id.Trim()).ToList();
        if (segments.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException(
                "--why received an empty package ID between commas; "
                    + "remove the stray comma or name a package for every slot.",
                nameof(raw)
            );
        }

        return segments
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Runs the restore-backed resolved-package scan for every discovered project concurrently,
    /// returning the results indexed by position in <paramref name="projectPaths"/> so callers can
    /// merge in discovery order regardless of completion order.
    ///
    /// <para>
    /// Scheduled exactly as the migration analysis schedules its own resolved pass, because these
    /// are the same subprocesses with the same hazards: two projects sharing a directory share one
    /// <c>obj/project.assets.json</c> and corrupt each other's query, so they are grouped by
    /// directory and run in sequence within their group; a solution that redirects its intermediate
    /// output somewhere the paths cannot see runs serially under the process-wide redirect lock;
    /// and the global gate keeps every scan in the process inside the advertised concurrency cap.
    /// See <see cref="ProjectDirectoryScanLock"/> and AnalysisHandler for the full history.
    /// </para>
    /// <para>
    /// Each project's warnings are captured against a scoped analyzer and returned alongside its
    /// result: workers must not write to the shared terminal, whose lines would follow completion
    /// order rather than discovery order on a console with no thread-safety contract. When
    /// <paramref name="graphService"/> is given, the resolved graph is also read while the
    /// project's directory lock is still held — projects sharing a directory overwrite each
    /// other's obj/project.assets.json at every restore, so a graph read after all restores
    /// finish would show only the last project's edges.
    /// </para>
    /// </summary>
    private static async Task<ConcurrentScanResult[]> ScanResolvedPackagesConcurrentlyAsync(
        Options options,
        ApplicationServices services,
        IReadOnlyList<string> projectPaths,
        bool includeTransitive,
        DependencyGraphService? graphService = null
    )
    {
        var results = await GroupedScanScheduler.RunAsync(
            projectPaths,
            options.ResolveScanParallelism(),
            async (_, projectPath, _) =>
            {
                var console = new BufferingConsoleService();
                // The graph reader reports malformed or unreadable assets files through the
                // same buffered console, so nothing inside the parallel window touches the
                // shared terminal.
                var scopedGraphService = graphService is null
                    ? null
                    : new DependencyGraphService(console);
                var (references, success) = await services
                    .WithConsole(console)
                    .ProjectAnalyzer.ScanResolvedPackagesAsync(projectPath, includeTransitive);
                var graph = success && scopedGraphService is not null
                    ? scopedGraphService.TryReadResolvedGraph(projectPath)
                    : null;
                return new ConcurrentScanResult(
                    references,
                    success,
                    graph,
                    console.Warnings
                );
            }
        );

        return results;
    }

    /// <summary>One project's concurrent-scan outcome, plus what it tried to report.</summary>
    private sealed record ConcurrentScanResult(
        List<Models.PackageReference> References,
        bool Success,
        Models.ProjectResolvedGraph? Graph,
        IReadOnlyList<string> Warnings
    );

    /// <summary>
    /// Reports a <c>--why</c> run that cannot produce a document and settles its exit code.
    ///
    /// <para>
    /// Under <c>--output Json</c> the prose goes to the caller's own console — not the silent
    /// console the scan ran with, through which an error message would simply vanish — and the
    /// router's standard failure payload goes out through
    /// <see cref="JsonOutputWriter.EmitFailureAsync"/>, which falls back to stdout when
    /// <c>--output-file</c> cannot be written. Other output modes keep their prose-only failure
    /// path: a JSON document interleaved with terminal rendering is its own kind of silence.
    /// </para>
    /// </summary>
    private static async Task<int> EmitWhyFailureAsync(
        Options options,
        int exitCode,
        string errorMessage,
        IConsoleService consoleService
    )
    {
        consoleService.Error(errorMessage);

        if (options.Output == OutputFormat.Json)
        {
            var formatter = new JsonFormatter();
            var operationResult = new OperationResult
            {
                // A multi-package request's failure must keep the discriminator its consumer
                // routed on — flipping to "why" would strand the error in a shape their parser
                // never selected.
                Operation =
                    SplitWhyPackageIds(options.Why!).Count > 1
                        ? "why-many"
                        : "why",
                Success = false,
                ExitCode = exitCode,
                Errors = [errorMessage],
            };
            await JsonOutputWriter.EmitFailureAsync(formatter.Format(operationResult), options);
        }

        return exitCode;
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
