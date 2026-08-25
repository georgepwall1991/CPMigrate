using System.Globalization;
using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services.Migration;
using CPMigrate.Services.Verify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates the CPM migration process.
/// </summary>
public class MigrationService
{
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly VersionResolver _versionResolver;
    private readonly PropsGenerator _propsGenerator;
    private readonly IBackupManager _backupManager;
    private readonly IConsoleService _consoleService;
    private readonly MigrationValidator _validator;
    private readonly MigrationDisplay _display;
    private readonly BackupCoordinator _backupCoordinator;
    private readonly RollbackHandler _rollbackHandler;
    private readonly ListBackupsHandler _listBackupsHandler;
    private readonly AnalysisHandler _analysisHandler;
    private readonly MigrationVerifier _verifier;
    private readonly ILogger<MigrationService> _logger;
    private readonly bool _quietMode;
    private readonly DiffFileCollector _diffCollector;

    /// <summary>
    /// Cached scan results from project discovery, to avoid redundant re-scanning.
    /// </summary>
    private Dictionary<string, List<PackageReference>>? _cachedProjectScans;

    public MigrationService(
        IConsoleService consoleService,
        IProjectAnalyzer? projectAnalyzer = null,
        VersionResolver? versionResolver = null,
        PropsGenerator? propsGenerator = null,
        IBackupManager? backupManager = null,
        IAnalysisService? analysisService = null,
        IFixService? fixService = null,
        bool quietMode = false,
        ILogger<MigrationService>? logger = null,
        MigrationVerifier? verifier = null,
        DiffFileCollector? diffFileCollector = null
    )
    {
        _consoleService = consoleService;
        _versionResolver = versionResolver ?? new VersionResolver(consoleService);
        _projectAnalyzer = projectAnalyzer ?? new ProjectAnalyzer(consoleService);
        _propsGenerator = propsGenerator ?? new PropsGenerator(_versionResolver);
        _backupManager = backupManager ?? new BackupManager();
        var resolvedAnalysisService =
            analysisService ?? CreateDefaultAnalysisService(consoleService);
        var resolvedFixService =
            fixService ?? CreateDefaultFixService(consoleService, _versionResolver);
        _validator = new MigrationValidator(consoleService);
        _display = new MigrationDisplay(consoleService);
        _backupCoordinator = new BackupCoordinator(_backupManager, consoleService, quietMode);
        _rollbackHandler = new RollbackHandler(consoleService, quietMode);
        _listBackupsHandler = new ListBackupsHandler(_backupManager, consoleService, quietMode);
        _analysisHandler = new AnalysisHandler(
            _projectAnalyzer,
            resolvedAnalysisService,
            resolvedFixService,
            consoleService,
            quietMode,
            DiscoverProjectsWithSpinnerAsync
        );
        _verifier =
            verifier
            ?? new MigrationVerifier(
                new AssetsGraphSnapshotService(
                    new DotNetCliService(),
                    new DependencyGraphService(consoleService),
                    consoleService
                )
            );
        _logger = logger ?? NullLogger<MigrationService>.Instance;
        _quietMode = quietMode;
        _diffCollector = diffFileCollector ?? new DiffFileCollector();
    }

    private static IAnalysisService CreateDefaultAnalysisService(IConsoleService console) =>
        new AnalysisService(AnalyzerCatalog.CreateDefault(console));

    private static IFixService CreateDefaultFixService(
        IConsoleService console,
        VersionResolver versionResolver
    ) => new FixService(console, FixerCatalog.CreateDefault(versionResolver));

    /// <summary>
    /// Executes the CPM migration based on the provided options.
    /// </summary>
    /// <param name="options">Migration options from CLI.</param>
    /// <returns>Migration result with exit code and statistics.</returns>
    public async Task<MigrationResult> ExecuteAsync(Options options)
    {
        if (!_quietMode)
        {
            _consoleService.WriteHeader();
        }

        if (!_validator.TryValidate(options, out var validationError))
        {
            return validationError!;
        }

        if (options.Rollback)
        {
            return await _rollbackHandler.ExecuteAsync(options);
        }

        if (options.ListBackups)
        {
            return await _listBackupsHandler.ExecuteAsync(options);
        }

        if (options.Analyze)
        {
            return await _analysisHandler.ExecuteAsync(options);
        }

        return await ExecuteMigrationAsync(options);
    }

    /// <summary>
    /// Executes the core migration workflow.
    /// </summary>
    private async Task<MigrationResult> ExecuteMigrationAsync(Options options)
    {
        if (!string.IsNullOrEmpty(options.DiffFile))
        {
            // Created before anything can fail: an absent artifact must mean the run crashed,
            // never that nothing changed.
            _diffCollector.Begin(options.DiffFile);
        }

        var (outputPath, propsPath) = MigrationValidator.GetOutputPaths(options);
        var propsFileExists = MigrationValidator.IsAlreadyMigrated(propsPath);
        string? backupPath = null;
        string? backupTimestamp = null;
        bool backupsCreated = false;

        try
        {
            var validationResult = await ValidateMigrationPrerequisitesAsync(
                options,
                outputPath,
                propsFileExists,
                propsPath
            );
            if (validationResult != null)
            {
                return validationResult;
            }

            var (basePath, projectPaths) = await DiscoverProjectsWithSpinnerAsync(options);
            if (projectPaths.Count == 0)
            {
                _consoleService.Error("No projects found to process.");
                return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
            }

            if (!_quietMode)
            {
                _display.ShowDiscoveredProjects(basePath, projectPaths);
            }
            // Before a single byte is written. A baseline captured after the rewrite would be a
            // comparison of the migration against itself, and one that cannot be captured at all is a
            // reason to stop rather than to proceed unmeasured — so this runs ahead of the backups and
            // leaves the tree untouched when it fails.
            GraphSnapshotResult? baseline = null;
            if (options.Verify)
            {
                baseline = await CaptureVerificationBaselineAsync(options, projectPaths, basePath);

                if (!baseline.RestoreSucceeded)
                {
                    var failure = MigrationVerifier.Compare(baseline, baseline, []);
                    _display.ShowVerificationReport(failure, options.VerifyStrict, _quietMode);
                    return new MigrationResult
                    {
                        ExitCode = ExitCodes.GraphDrift,
                        Verification = failure,
                    };
                }
            }

            var (success, packages, existingPackages, propsFileExisted) = LoadPackageState(
                options,
                propsPath,
                propsFileExists
            );
            if (!success)
            {
                return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
            }

            backupPath = SetupBackupDirectory(options);
            backupTimestamp =
                !options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath)
                    ? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                    : null;

            var backupEntries = await CreateBackupsAndProcessProjectsAsync(
                options,
                projectPaths,
                packages,
                propsPath,
                propsFileExists,
                backupPath,
                backupTimestamp
            );
            backupsCreated = backupEntries.Count > 0;

            var conflicts = VersionResolver.DetectConflicts(packages, existingPackages);

            // CRITICAL: Write manifest BEFORE conflict resolution so rollback can work if resolution fails
            await WriteBackupManifestAsync(
                options,
                backupEntries,
                backupPath,
                propsPath,
                propsFileExisted,
                backupTimestamp
            );

            var conflictError = await HandleConflictsWithRollbackAsync(
                options,
                packages,
                conflicts,
                backupsCreated,
                backupPath
            );
            if (conflictError != null)
            {
                return conflictError;
            }

            var propsFilePath = await GeneratePropsFileAsync(options, packages);

            var (verification, verificationWarnings) = baseline is null
                ? (null, null)
                : await VerifyMigrationAsync(
                    options,
                    baseline,
                    projectPaths,
                    basePath,
                    packages,
                    conflicts,
                    backupsCreated,
                    backupPath
                );

            await FinalizeMigrationAsync(
                options,
                backupPath,
                propsFilePath,
                projectPaths.Count,
                packages.Count,
                conflicts.Count,
                verification
            );

            return new MigrationResult
            {
                ProjectsProcessed = projectPaths.Count,
                PackagesCentralized = packages.Count,
                ConflictsResolved = conflicts.Count,
                PropsFilePath = propsFilePath,
                BackupPath = backupPath,
                Warnings = verificationWarnings,
                WasDryRun = options.DryRun,
                ExitCode =
                    verification is null || verification.Passed(options.VerifyStrict)
                        ? ExitCodes.Success
                        : ExitCodes.GraphDrift,
                Verification = verification,
            };
        }
        catch (Exception ex)
        {
            await HandleMigrationErrorAsync(
                ex,
                options,
                backupsCreated,
                options.DryRun,
                backupPath
            );
            throw; // Re-throw to be handled by Program.cs or caller
        }
    }

    /// <summary>
    /// Captures what the solution resolves to as it stands, before anything is written.
    /// </summary>
    private async Task<GraphSnapshotResult> CaptureVerificationBaselineAsync(
        Options options,
        List<string> projectPaths,
        string basePath
    )
    {
        if (!_quietMode)
        {
            _consoleService.Info(
                "Capturing the resolved dependency graph before migrating (dotnet restore)..."
            );
        }

        return await _verifier.CaptureAsync(RestoreTarget(options), projectPaths, basePath);
    }

    /// <summary>
    /// The path to restore, made absolute.
    /// </summary>
    /// <remarks>
    /// It has to be absolute. <see cref="IDotNetCliService.RunRestoreAsync"/> runs from the target's
    /// own directory while passing the path through as the argument, so a nested relative target —
    /// <c>-s src/Solution.slnx</c>, the ordinary way to point at a solution — resolves to
    /// <c>src/src/Solution.slnx</c> and the restore fails. That failure lands on the baseline, before
    /// anything is written, so it reads as "this solution does not restore" about a solution that
    /// restores perfectly well. Cross-review caught it.
    /// </remarks>
    internal static string RestoreTarget(Options options)
    {
        return Path.GetFullPath(options.GetDiscoveryTargetPath());
    }

    /// <summary>
    /// Captures the graph the migration produced, compares it against the baseline, and undoes the
    /// migration when the comparison cannot account for what moved.
    /// </summary>
    private async Task<(VerificationReport Report, IReadOnlyList<string>? Warnings)>
        VerifyMigrationAsync(
        Options options,
        GraphSnapshotResult baseline,
        List<string> projectPaths,
        string basePath,
        Dictionary<string, HashSet<string>> packages,
        List<string> conflicts,
        bool backupsCreated,
        string? backupPath
    )
    {
        if (!_quietMode)
        {
            _consoleService.Info("Verifying the resolved dependency graph (dotnet restore)...");
        }

        var after = await _verifier.CaptureAsync(RestoreTarget(options), projectPaths, basePath);
        var report = MigrationVerifier.Compare(
            baseline,
            after,
            RecordDecisions(options, packages, conflicts, basePath)
        );

        _display.ShowVerificationReport(report, options.VerifyStrict, _quietMode);

        if (!report.ShouldRollBack)
        {
            return (report, null);
        }

        // Recorded from what happened, not from what was supposed to. A payload asserting the tree was
        // restored when it was not is worse than one admitting it could not be.
        var (rolledBack, rollbackWarnings) = await RollBackUnverifiedMigrationAsync(
            options,
            backupsCreated,
            backupPath
        );

        return (report with { RolledBack = rolledBack }, rollbackWarnings);
    }

    /// <summary>
    /// Undoes a migration the verification could not vouch for.
    /// </summary>
    /// <remarks>
    /// Unattended by design, unlike the rollback offered after an error, and that is why it forces
    /// past the confirmation rather than inheriting <c>--force</c> from the caller. The rollback
    /// handler declines on a non-interactive terminal without <c>--force</c>, and declines outright
    /// under <c>--quiet</c> with a machine-readable format — which together describe every CI run,
    /// the exact place this protection is worth having. Passing <c>--verify</c> <em>is</em> the
    /// consent: it asks to be protected from a change nobody has read, and a prompt nothing answers
    /// would leave precisely that change on disk while the report claimed otherwise.
    /// </remarks>
    /// <returns>Whether the working tree was actually restored.</returns>
    private async Task<(bool Restored, IReadOnlyList<string>? Warnings)>
        RollBackUnverifiedMigrationAsync(Options options, bool backupsCreated, string? backupPath)
    {
        if (!backupsCreated || string.IsNullOrEmpty(backupPath))
        {
            _consoleService.Warning(
                "No backup is available, so the migration could not be undone. The working tree still "
                    + "holds changes this run could not verify — use git to discard them."
            );
            return (false, null);
        }

        _consoleService.Warning("Rolling the migration back.");

        var rollbackOptions = CreateRollbackOptions(options, backupPath);
        rollbackOptions.Force = true;

        var result = await _rollbackHandler.ExecuteAsync(rollbackOptions);

        if (result.ExitCode != ExitCodes.Success)
        {
            return (false, result.Warnings);
        }

        // The exit code is not sufficient. RollbackHandler catches a failure to delete a props file
        // this run generated — locked, read-only, whatever — and still succeeds, so a tree with the
        // migration's props file still in force would be reported as restored. Checked rather than
        // assumed: the point of the flag is that someone can stop looking.
        var (_, propsPath) = MigrationValidator.GetOutputPaths(options);

        if (File.Exists(propsPath))
        {
            _consoleService.Warning(
                $"Rollback restored the project files but {Path.GetFileName(propsPath)} could not be "
                    + "removed, so central package management is still in force. Delete it by hand."
            );
            return (false, result.Warnings);
        }

        return (true, result.Warnings);
    }

    /// <summary>
    /// Records which version won for each conflicted package, out of what, and at whose direction.
    /// </summary>
    /// <remarks>
    /// The candidates come from the per-project scan rather than from <paramref name="packages"/>,
    /// which by this point has been collapsed to the winning version under
    /// <c>--interactive-conflicts</c> — and which never held the projects each version came from.
    /// A package brought in from an existing props file by <c>--merge</c> has no scan entry, so its
    /// candidate list is whatever the projects declare; the resolved version, which is what attribution
    /// keys on, is unaffected.
    ///
    /// <para><b>Known limitation, stated rather than implied.</b> The cache is filled from the declared
    /// references before <c>--include-transitive</c> adds anything, so a conflict that exists only
    /// among transitive packages gets an empty candidate list — the receipt still reports the version
    /// that won and still attributes the change, but cannot show what it won against. Filling it would
    /// mean re-shaping the scan cache that the migration writer itself reads, which is a larger change
    /// than an informational column is worth. Found by cross-review.</para>
    /// </remarks>
    private List<MigrationDecision> RecordDecisions(
        Options options,
        Dictionary<string, HashSet<string>> packages,
        List<string> conflicts,
        string basePath
    )
    {
        var declarations = BuildDeclarationsByPackage(basePath);
        var source = ResolveDecisionSource(options);

        return
        [
            .. conflicts
                .Where((string package) => packages.ContainsKey(package))
                .Select(
                    (string package) =>
                        new MigrationDecision(
                            package,
                            // Normalized, because the graph it will be matched against is. A single
                            // remaining candidate is returned verbatim by ResolveVersion — so an
                            // interactively chosen "4.3" stays "4.3" here while the assets file says
                            // "4.3.0", and attribution rejects the very version the user picked.
                            // Cross-review caught it.
                            VersionText.Normalize(
                                _versionResolver.ResolveVersion(
                                    packages[package],
                                    options.ConflictStrategy
                                )
                            ),
                            declarations.TryGetValue(package, out var candidates) ? candidates : [],
                            source
                        )
                ),
        ];
    }

    private ConflictDecisionSource ResolveDecisionSource(Options options)
    {
        // --interactive-conflicts on a terminal that cannot prompt falls back to the configured
        // strategy without collapsing anything, so recording it as an interactive choice would
        // credit a decision to a person who was never asked.
        if (options.InteractiveConflicts && _consoleService.IsInteractive)
        {
            return ConflictDecisionSource.Interactive;
        }

        return options.ConflictStrategy == ConflictStrategy.Lowest
            ? ConflictDecisionSource.Lowest
            : ConflictDecisionSource.Highest;
    }

    private Dictionary<string, List<VersionCandidate>> BuildDeclarationsByPackage(string basePath)
    {
        Dictionary<string, Dictionary<string, List<string>>> byPackage = new(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var (projectPath, references) in _cachedProjectScans ?? [])
        {
            foreach (var reference in references)
            {
                if (!byPackage.TryGetValue(reference.PackageName, out var versions))
                {
                    versions = new Dictionary<string, List<string>>(
                        StringComparer.OrdinalIgnoreCase
                    );
                    byPackage[reference.PackageName] = versions;
                }

                if (!versions.TryGetValue(reference.Version, out var projects))
                {
                    projects = [];
                    versions[reference.Version] = projects;
                }

                // The same relative identity the graph and the receipt use. A bare file name cannot
                // tell src/Api/Api.csproj from tests/Api/Api.csproj, so a candidate list naming both
                // "Api.csproj" leaves a reviewer unable to see which project declared what.
                projects.Add(ProjectPackageInfo.ProjectId(basePath, projectPath));
            }
        }

        return byPackage.ToDictionary(
            (KeyValuePair<string, Dictionary<string, List<string>>> entry) => entry.Key,
            (KeyValuePair<string, Dictionary<string, List<string>>> entry) =>
                entry
                    .Value.Select(version => new VersionCandidate(
                        version.Key,
                        [.. version.Value.OrderBy(p => p, StringComparer.Ordinal)]
                    ))
                    .OrderBy(candidate => candidate.Version, StringComparer.Ordinal)
                    .ToList(),
            StringComparer.OrdinalIgnoreCase
        );
    }

    private async Task<MigrationResult?> ValidateMigrationPrerequisitesAsync(
        Options options,
        string outputPath,
        bool propsFileExists,
        string propsPath
    )
    {
        if (!options.DryRun)
        {
            var directoryError = _validator.ValidateOutputDirectory(outputPath);
            if (directoryError != null)
            {
                return directoryError;
            }

            await _validator.CheckForUnstagedChangesAsync(outputPath);
        }

        if (propsFileExists && !options.MergeExisting)
        {
            return _display.CreateAlreadyMigratedResult(propsPath);
        }

        if (!_quietMode)
        {
            _display.ShowDryRunBannerIfNeeded(options);
        }

        return null;
    }

    private (
        bool Success,
        Dictionary<string, HashSet<string>> Packages,
        Dictionary<string, HashSet<string>> ExistingPackages,
        bool PropsFileExisted
    ) LoadPackageState(Options options, string propsPath, bool propsFileExists)
    {
        Dictionary<string, HashSet<string>> packages = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> existingPackages = new(
            StringComparer.OrdinalIgnoreCase
        );
        bool hadConditionalPackageVersions = false;

        if (propsFileExists && options.MergeExisting)
        {
            // If merging is requested but we can't parse the existing file, we must abort
            if (
                !TryLoadExistingPropsPackages(
                    propsPath,
                    existingPackages,
                    out var existingCount,
                    out hadConditionalPackageVersions
                )
            )
            {
                return (false, packages, existingPackages, propsFileExists);
            }

            foreach (var kvp in existingPackages)
            {
                packages.Add(kvp.Key, new HashSet<string>(kvp.Value));
            }

            if (!_quietMode)
            {
                _consoleService.Info(
                    $"Loaded {existingCount} package(s) from existing Directory.Packages.props."
                );
            }

            if (hadConditionalPackageVersions && !_quietMode)
            {
                _consoleService.Warning(
                    "Conditional PackageVersion entries detected; merge will normalize versions."
                );
            }
        }

        return (true, packages, existingPackages, propsFileExists);
    }

    private async Task<List<BackupEntry>> CreateBackupsAndProcessProjectsAsync(
        Options options,
        List<string> projectPaths,
        Dictionary<string, HashSet<string>> packages,
        string propsPath,
        bool propsFileExists,
        string? backupPath,
        string? backupTimestamp
    )
    {
        List<BackupEntry> backupEntries = [];
        var propsBackupEntry = CreatePropsBackup(
            options,
            propsFileExists,
            propsPath,
            backupPath,
            backupTimestamp
        );

        if (propsBackupEntry != null)
        {
            backupEntries.Add(propsBackupEntry);
        }

        var projectBackups = await ProcessProjectsWithProgressAsync(
            options,
            projectPaths,
            packages,
            backupPath,
            backupTimestamp
        );
        if (projectBackups.Count > 0)
        {
            backupEntries.AddRange(projectBackups);
        }

        return backupEntries;
    }

    private bool TryLoadExistingPropsPackages(
        string propsFilePath,
        Dictionary<string, HashSet<string>> packages,
        out int existingPackageCount,
        out bool hasConditionalPackageVersions
    )
    {
        existingPackageCount = 0;
        hasConditionalPackageVersions = false;

        try
        {
            var existingPackages = PropsGenerator.ReadExistingPackageVersions(
                propsFilePath,
                out hasConditionalPackageVersions
            );
            existingPackageCount = existingPackages.Count;

            foreach (var kvp in existingPackages)
            {
                packages.Add(kvp.Key, new HashSet<string>(kvp.Value));
            }

            return true;
        }
        catch (Exception ex)
        {
            _consoleService.Error(
                $"Failed to read existing Directory.Packages.props: {ex.Message}"
            );
            return false;
        }
    }

    /// <summary>
    /// Discovers projects with a spinner or silently in quiet mode.
    /// </summary>
    private async Task<(
        string BasePath,
        List<string> ProjectPaths
    )> DiscoverProjectsWithSpinnerAsync(Options options)
    {
        if (_quietMode)
        {
            return DiscoverProjects(options);
        }

        return await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(
                "Discovering projects...",
                async ctx =>
                {
                    await Task.Delay(100);
                    return DiscoverProjects(options);
                }
            );
    }

    /// <summary>
    /// Sets up the backup directory if needed.
    /// </summary>
    private string? SetupBackupDirectory(Options options) =>
        _backupCoordinator.SetupBackupDirectory(MigrationRequest.FromOptions(options));

    /// <summary>
    /// Handles version conflicts and returns an error result if strategy is Fail.
    /// </summary>
    private MigrationResult? HandleVersionConflicts(
        Options options,
        Dictionary<string, HashSet<string>> packages,
        List<string> conflicts
    )
    {
        if (conflicts.Count == 0)
        {
            return null;
        }

        if (!_quietMode)
        {
            _consoleService.WriteConflictsTable(packages, conflicts, options.ConflictStrategy);
        }

        if (options.ConflictStrategy == ConflictStrategy.Fail)
        {
            return HandleFailStrategy();
        }

        if (options.InteractiveConflicts)
        {
            ResolveConflictsInteractively(options, packages, conflicts);
        }

        return null;
    }

    private MigrationResult HandleFailStrategy()
    {
        _consoleService.Error("Version conflicts detected and --conflict-strategy is set to Fail.");
        if (!_quietMode)
        {
            _consoleService.WriteMarkup(
                "[dim]Resolve the conflicts manually or use --conflict-strategy Highest|Lowest.[/]\n"
            );
        }
        return new MigrationResult { ExitCode = ExitCodes.VersionConflict };
    }

    private void ResolveConflictsInteractively(
        Options options,
        Dictionary<string, HashSet<string>> packages,
        List<string> conflicts
    )
    {
        // --interactive-conflicts on a terminal that cannot prompt would throw on the first
        // package. Fall back to the configured --conflict-strategy — the same deterministic
        // resolution the run would have used without the flag — and say so once, rather than
        // failing a migration that has a perfectly good automatic answer.
        if (!_consoleService.IsInteractive)
        {
            _consoleService.Warning(
                $"--interactive-conflicts needs a prompt-capable terminal; resolving {conflicts.Count} conflict(s) with --conflict-strategy {options.ConflictStrategy} instead."
            );
            return;
        }

        _consoleService.WriteLine();
        _consoleService.Banner("INTERACTIVE CONFLICT RESOLUTION");
        _consoleService.WriteLine();
        _consoleService.Info("Select the version to use for each package with conflicts:");
        _consoleService.WriteLine();

        var usageCounts = BuildPackageUsageCounts(options);

        foreach (var packageName in conflicts)
        {
            ProcessConflictChoice(options, packages, packageName, usageCounts);
        }

        _consoleService.WriteLine();
        _consoleService.Success("All conflicts resolved interactively.");
    }

    private Dictionary<string, Dictionary<string, int>> BuildPackageUsageCounts(Options options)
    {
        var usageCounts = new Dictionary<string, Dictionary<string, int>>(
            StringComparer.OrdinalIgnoreCase
        );

        // Use cached scan results if available to avoid redundant project re-scanning
        if (_cachedProjectScans != null)
        {
            foreach (var (_, refs) in _cachedProjectScans)
            {
                foreach (var reference in refs)
                {
                    AddToUsageCounts(usageCounts, reference.PackageName, reference.Version);
                }
            }

            return usageCounts;
        }

        _logger.LogDebug("No cached scan results available, re-scanning projects for usage counts");
        var (_, projectPaths) = DiscoverProjects(options);

        foreach (var path in projectPaths)
        {
            var (refs, _) = _projectAnalyzer.ScanProjectPackages(path);
            foreach (var reference in refs)
            {
                AddToUsageCounts(usageCounts, reference.PackageName, reference.Version);
            }
        }

        return usageCounts;
    }

    private static void AddToUsageCounts(
        Dictionary<string, Dictionary<string, int>> usageCounts,
        string packageName,
        string version
    )
    {
        if (!usageCounts.ContainsKey(packageName))
        {
            usageCounts[packageName] = [];
        }

        if (!usageCounts[packageName].ContainsKey(version))
        {
            usageCounts[packageName][version] = 0;
        }

        usageCounts[packageName][version]++;
    }

    private void ProcessConflictChoice(
        Options options,
        Dictionary<string, HashSet<string>> packages,
        string packageName,
        Dictionary<string, Dictionary<string, int>> usageCounts
    )
    {
        if (!packages.TryGetValue(packageName, out var versions))
        {
            return;
        }

        var versionList = versions.OrderByDescending(v => v).ToList();
        var recommended = _versionResolver.ResolveVersion(versions, options.ConflictStrategy);

        var choices = BuildVersionChoices(packageName, versionList, recommended, usageCounts);
        var selected = _consoleService.AskSelection($"Version for {packageName}?", choices);
        var selectedVersion = selected.Split(' ')[0];

        packages[packageName] = [selectedVersion];
    }

    private static List<string> BuildVersionChoices(
        string packageName,
        List<string> versions,
        string recommended,
        Dictionary<string, Dictionary<string, int>> usageCounts
    )
    {
        return versions
            .Select(v =>
            {
                var count = GetVersionUsageCount(usageCounts, packageName, v);
                var label = $"{v} (Used by {count} project{(count == 1 ? "" : "s")})";

                if (v == recommended)
                {
                    label += " [springgreen1]**Recommended**[/]";
                }

                return label;
            })
            .ToList();
    }

    private static int GetVersionUsageCount(
        Dictionary<string, Dictionary<string, int>> usageCounts,
        string packageName,
        string version
    )
    {
        return usageCounts.ContainsKey(packageName) && usageCounts[packageName].ContainsKey(version)
            ? usageCounts[packageName][version]
            : 1;
    }

    /// <summary>
    /// Writes the backup manifest if backups were created.
    /// </summary>
    private async Task WriteBackupManifestAsync(
        Options options,
        List<BackupEntry> backupEntries,
        string? backupPath,
        string propsFilePath,
        bool propsFileExisted,
        string? backupTimestamp
    ) =>
        await _backupCoordinator.WriteManifestAsync(
            MigrationRequest.FromOptions(options),
            backupEntries,
            backupPath,
            propsFilePath,
            propsFileExisted,
            backupTimestamp
        );

    /// <summary>
    /// Manages .gitignore for the backup directory.
    /// </summary>
    private async Task ManageGitIgnoreAsync(Options options, string? backupPath) =>
        await _backupCoordinator.ManageGitIgnoreAsync(
            MigrationRequest.FromOptions(options),
            backupPath
        );

    private (string BasePath, List<string> ProjectPaths) DiscoverProjects(Options options)
    {
        if (options.HasExplicitProjectPath)
        {
            return _projectAnalyzer.DiscoverProjectFromPath(options.ProjectFileDir);
        }

        return _projectAnalyzer.DiscoverProjectsFromSolution(options.GetDiscoveryTargetPath());
    }

    private async Task<List<BackupEntry>> ProcessProjectsWithProgressAsync(
        Options options,
        List<string> projectPaths,
        Dictionary<string, HashSet<string>> packages,
        string? backupPath,
        string? backupTimestamp
    )
    {
        List<BackupEntry> backupEntries = [];

        // Process without progress bar in quiet mode
        if (_quietMode)
        {
            foreach (var projectFilePath in projectPaths)
            {
                var backupEntry = await ProcessSingleProjectAsync(
                    options,
                    projectFilePath,
                    packages,
                    backupPath,
                    backupTimestamp,
                    null
                );
                if (backupEntry != null)
                {
                    backupEntries.Add(backupEntry);
                }
            }
            return backupEntries;
        }

        await AnsiConsole
            .Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            )
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(
                    "[cyan]Processing projects[/]",
                    maxValue: projectPaths.Count
                );

                foreach (var projectFilePath in projectPaths)
                {
                    var projectName = Path.GetFileName(projectFilePath);
                    task.Description =
                        $"[cyan]Processing[/] [white]{Markup.Escape(projectName)}[/]";

                    var backupEntry = await ProcessSingleProjectAsync(
                        options,
                        projectFilePath,
                        packages,
                        backupPath,
                        backupTimestamp,
                        task
                    );
                    if (backupEntry != null)
                    {
                        backupEntries.Add(backupEntry);
                    }

                    task.Increment(1);
                    await Task.Delay(50); // Small delay for visual smoothness
                }

                task.Description = "[green]Processing complete[/]";
            });

        _consoleService.WriteLine();
        return backupEntries;
    }

    private async Task<string> GeneratePropsFileAsync(
        Options options,
        Dictionary<string, HashSet<string>> packages
    )
    {
        var (_, propsFilePath) = MigrationValidator.GetOutputPaths(options);
        var shouldMerge = options.MergeExisting && File.Exists(propsFilePath);

        if (shouldMerge)
        {
            return await MergeAndWritePropsFileAsync(options, propsFilePath, packages);
        }

        return await CreateNewPropsFileAsync(options, propsFilePath, packages);
    }

    private async Task<string> MergeAndWritePropsFileAsync(
        Options options,
        string propsFilePath,
        Dictionary<string, HashSet<string>> packages
    )
    {
        var (mergedContent, addedCount, updatedCount, _) = _propsGenerator.MergeExisting(
            propsFilePath,
            packages,
            options.ConflictStrategy
        );

        if (options.DryRun)
        {
            string? diff = null;
            if (options.Diff || _diffCollector.IsEnabled)
            {
                var original = File.Exists(propsFilePath)
                    ? await File.ReadAllTextAsync(propsFilePath)
                    : null;
                diff = UnifiedDiffGenerator.Generate(original, mergedContent, propsFilePath);
            }

            if (!_quietMode)
            {
                _consoleService.WriteLine();
                _consoleService.DryRun($"Would update: {propsFilePath}");
                _consoleService.WriteLine();
                if (diff != null && options.Diff)
                {
                    _consoleService.WriteDiff(diff);
                }
                else
                {
                    _consoleService.WritePropsPreview(mergedContent);
                }
            }

            // A no-op unless --diff-file is set; the diff is produced either way once it is.
            _diffCollector.Append(diff);
        }
        else
        {
            await FileHelper.WriteAtomicAsync(propsFilePath, mergedContent);
            if (!_quietMode)
            {
                _consoleService.WriteMarkup(
                    $"\n[green]:page_facing_up: Updated:[/] [cyan]{Markup.Escape(propsFilePath)}[/]\n"
                );
                if (addedCount > 0 || updatedCount > 0)
                {
                    _consoleService.Dim($"Added {addedCount} package(s), updated {updatedCount}.");
                }
            }
        }

        return propsFilePath;
    }

    private async Task<string> CreateNewPropsFileAsync(
        Options options,
        string propsFilePath,
        Dictionary<string, HashSet<string>> packages
    )
    {
        var updatedPackagePropsContent = _propsGenerator.Generate(
            packages,
            options.ConflictStrategy
        );

        if (options.DryRun)
        {
            string? diff = null;
            if (options.Diff || _diffCollector.IsEnabled)
            {
                diff = UnifiedDiffGenerator.Generate(
                    null,
                    updatedPackagePropsContent,
                    propsFilePath
                );
            }

            if (!_quietMode)
            {
                _consoleService.WriteLine();
                _consoleService.DryRun($"Would create: {propsFilePath}");
                _consoleService.WriteLine();
                if (diff != null && options.Diff)
                {
                    _consoleService.WriteDiff(diff);
                }
                else
                {
                    _consoleService.WritePropsPreview(updatedPackagePropsContent);
                }
            }

            _diffCollector.Append(diff);
        }
        else
        {
            await FileHelper.WriteAtomicAsync(propsFilePath, updatedPackagePropsContent);
            if (!_quietMode)
            {
                _consoleService.WriteMarkup(
                    $"\n[green]:page_facing_up: Generated:[/] [cyan]{Markup.Escape(propsFilePath)}[/]\n"
                );
            }
        }

        return propsFilePath;
    }

    /// <summary>
    /// Creates a backup of the existing Directory.Packages.props file if needed.
    /// </summary>
    private BackupEntry? CreatePropsBackup(
        Options options,
        bool propsFileExists,
        string propsPath,
        string? backupPath,
        string? backupTimestamp
    ) =>
        _backupCoordinator.CreatePropsBackup(
            MigrationRequest.FromOptions(options),
            propsFileExists,
            propsPath,
            backupPath,
            backupTimestamp
        );

    /// <summary>
    /// Handles version conflicts and offers rollback if migration has already modified files.
    /// </summary>
    private async Task<MigrationResult?> HandleConflictsWithRollbackAsync(
        Options options,
        Dictionary<string, HashSet<string>> packages,
        List<string> conflicts,
        bool backupsCreated,
        string? backupPath
    )
    {
        var conflictError = HandleVersionConflicts(options, packages, conflicts);
        if (conflictError == null)
        {
            return null;
        }

        // If we fail here due to conflicts, we should warn the user
        // ProcessProjectsWithProgressAsync ALREADY wrote the modified project files
        if (!options.DryRun)
        {
            _consoleService.Warning(
                "Migration interrupted during conflict resolution. Project files have already been modified."
            );

            if (backupsCreated && !string.IsNullOrEmpty(backupPath))
            {
                if (
                    ShouldProceedWithAutomaticRollback(
                        options,
                        "Would you like to rollback changes using the created backup?"
                    )
                )
                {
                    await _rollbackHandler.ExecuteAsync(CreateRollbackOptions(options, backupPath));
                }
            }
            else
            {
                _consoleService.Info(
                    "Note: No backups were created (or backup was disabled), so automatic rollback is unavailable."
                );
            }
        }

        return conflictError;
    }

    /// <summary>
    /// Finalizes migration by writing manifest, managing .gitignore, and showing results.
    /// </summary>
    private async Task FinalizeMigrationAsync(
        Options options,
        string? backupPath,
        string propsFilePath,
        int projectCount,
        int packageCount,
        int conflictCount,
        VerificationReport? verification
    )
    {
        var failedVerification =
            verification is not null && !verification.Passed(options.VerifyStrict);

        // Two separate questions, and cross-review caught them being answered as one.
        //
        // The .gitignore entry belongs to the *backup*, so it is written whenever a backup survives —
        // including a --verify-strict failure, where the tree is deliberately left in place, and a
        // rollback that could not run. Skipping it there left an un-ignored backup directory beside a
        // failed run, which is precisely when someone commits it by accident. It is skipped only when
        // the rollback actually happened, because then there is nothing left to ignore.
        if (!failedVerification || verification?.RolledBack != true)
        {
            // Manifest already written before conflict resolution
            await ManageGitIgnoreAsync(options, backupPath);
        }

        // The summary, by contrast, is about the migration: "Migration completed successfully! 🎉"
        // over one that was just rolled back, or one whose effect on the build could not be
        // established, would be the loudest possible version of the failure this feature exists to
        // prevent. The verification report has already said what happened.
        if (failedVerification)
        {
            return;
        }

        if (!_quietMode)
        {
            _display.ShowMigrationSummary(
                projectCount,
                packageCount,
                conflictCount,
                propsFilePath,
                options.DryRun
            );
            _display.ShowPostMigrationGuidance(options, propsFilePath);
        }
    }

    /// <summary>
    /// Handles migration errors and offers automatic rollback if backups exist.
    /// </summary>
    private async Task HandleMigrationErrorAsync(
        Exception ex,
        Options options,
        bool backupsCreated,
        bool dryRun,
        string? backupPath
    )
    {
        _consoleService.Error($"\nAn error occurred during migration: {ex.Message}");

        if (backupsCreated && !dryRun && !string.IsNullOrEmpty(backupPath))
        {
            _consoleService.Warning("Project files may have been partially modified.");
            if (
                ShouldProceedWithAutomaticRollback(
                    options,
                    "Would you like to attempt an automatic rollback to the last backup?"
                )
            )
            {
                var rollbackResult = await _rollbackHandler.ExecuteAsync(
                    CreateRollbackOptions(options, backupPath)
                );

                // The error path rethrows, discarding this result — and quiet or machine-readable
                // runs silenced the handler's own note. Stderr is the one channel guaranteed to
                // survive both.
                RollbackWarningSink.Write(rollbackResult.Warnings);
            }
        }
    }

    private bool ShouldProceedWithAutomaticRollback(Options options, string prompt)
    {
        if (options.Force)
        {
            return true;
        }

        if (options.Output.IsMachineReadable())
        {
            return false;
        }

        if (_quietMode)
        {
            return true;
        }

        // This prompt only fires after a migration has already failed, so the choice is between
        // restoring the backup and leaving the tree half-migrated. Unlike the other confirmations,
        // the safe answer here is yes — matching the --quiet behaviour directly above.
        if (!_consoleService.IsInteractive)
        {
            _consoleService.Warning(
                "Migration failed; rolling back automatically (no prompt available on this terminal)."
            );
            return true;
        }

        return _consoleService.AskConfirmation(prompt);
    }

    private static Options CreateRollbackOptions(Options sourceOptions, string backupPath)
    {
        var rollbackBackupDir = ResolveRollbackBackupDir(backupPath, sourceOptions.BackupDir);

        return new Options
        {
            BackupDir = rollbackBackupDir,
            Rollback = true,
            Force = sourceOptions.Force,
            Output = sourceOptions.Output,
            Quiet = sourceOptions.Quiet,
        };
    }

    private static string ResolveRollbackBackupDir(string backupPath, string fallbackBackupDir)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return fallbackBackupDir;
        }

        var normalizedPath = backupPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );
        if (
            string.Equals(
                Path.GetFileName(normalizedPath),
                ".cpmigrate_backup",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return Path.GetDirectoryName(normalizedPath) ?? fallbackBackupDir;
        }

        return normalizedPath;
    }

    /// <summary>
    /// Processes a single project: creates backup, modifies project file, and handles transitive dependencies.
    /// </summary>
    private async Task<BackupEntry?> ProcessSingleProjectAsync(
        Options options,
        string projectFilePath,
        Dictionary<string, HashSet<string>> packages,
        string? backupPath,
        string? backupTimestamp,
        Spectre.Console.ProgressTask? progressTask
    )
    {
        BackupEntry? backupEntry = null;

        // Create backup
        if (!options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath))
        {
            backupEntry = _backupManager.CreateBackupForProject(
                options,
                projectFilePath,
                backupPath,
                backupTimestamp
            );
        }

        // Cache scan results before processing (for use by interactive conflict resolution)
        var (scannedRefs, _) = _projectAnalyzer.ScanProjectPackages(projectFilePath);
        _cachedProjectScans ??= new Dictionary<string, List<PackageReference>>(
            StringComparer.OrdinalIgnoreCase
        );
        _cachedProjectScans[projectFilePath] = scannedRefs;

        // Process project file
        var projectFileContent = ProjectAnalyzer.ProcessProject(
            projectFilePath,
            packages,
            options.KeepAttributes
        );

        // Handle transitive dependencies if requested
        if (options.IncludeTransitive)
        {
            if (progressTask != null)
            {
                var projectName = Path.GetFileName(projectFilePath);
                progressTask.Description =
                    $"[cyan]Scanning transitive[/] [white]{Markup.Escape(projectName)}[/]";
            }

            await AddTransitivePackagesAsync(projectFilePath, packages);
        }

        // Write modified project file
        if (!options.DryRun)
        {
            await FileHelper.WriteAtomicAsync(projectFilePath, projectFileContent);
        }

        return backupEntry;
    }

    /// <summary>
    /// Scans and adds transitive package dependencies to the packages dictionary.
    /// </summary>
    private async Task AddTransitivePackagesAsync(
        string projectFilePath,
        Dictionary<string, HashSet<string>> packages
    )
    {
        var (transitiveRefs, transitiveSuccess) =
            await _projectAnalyzer.ScanTransitivePackagesAsync(projectFilePath);
        if (!transitiveSuccess)
        {
            return;
        }

        foreach (var tr in transitiveRefs)
        {
            if (packages.TryGetValue(tr.PackageName, out var versions))
            {
                versions.Add(tr.Version);
            }
            else
            {
                packages.Add(tr.PackageName, new HashSet<string> { tr.Version });
            }
        }
    }
}
