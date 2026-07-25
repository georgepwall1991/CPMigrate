using System.Globalization;
using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services.Migration;
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
    private readonly ILogger<MigrationService> _logger;
    private readonly bool _quietMode;

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
        ILogger<MigrationService>? logger = null)
    {
        _consoleService = consoleService;
        _versionResolver = versionResolver ?? new VersionResolver(consoleService);
        _projectAnalyzer = projectAnalyzer ?? new ProjectAnalyzer(consoleService);
        _propsGenerator = propsGenerator ?? new PropsGenerator(_versionResolver);
        _backupManager = backupManager ?? new BackupManager();
        var resolvedAnalysisService = analysisService ?? CreateDefaultAnalysisService(consoleService);
        var resolvedFixService = fixService ?? CreateDefaultFixService(consoleService, _versionResolver);
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
            DiscoverProjectsWithSpinnerAsync);
        _logger = logger ?? NullLogger<MigrationService>.Instance;
        _quietMode = quietMode;
    }

    private static IAnalysisService CreateDefaultAnalysisService(IConsoleService console) =>
        new AnalysisService(AnalyzerCatalog.CreateDefault(console));

    private static IFixService CreateDefaultFixService(IConsoleService console, VersionResolver versionResolver) =>
        new FixService(console, FixerCatalog.CreateDefault(versionResolver));

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
        var (outputPath, propsPath) = MigrationValidator.GetOutputPaths(options);
        var propsFileExists = MigrationValidator.IsAlreadyMigrated(propsPath);
        string? backupPath = null;
        string? backupTimestamp = null;
        bool backupsCreated = false;

        try
        {
            var validationResult = await ValidateMigrationPrerequisitesAsync(options, outputPath, propsFileExists, propsPath);
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
            var (success, packages, existingPackages, propsFileExisted) = LoadPackageState(options, propsPath, propsFileExists);
            if (!success)
            {
                return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
            }

            backupPath = SetupBackupDirectory(options);
            backupTimestamp = !options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath)
                ? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                : null;

            var backupEntries = await CreateBackupsAndProcessProjectsAsync(
                options, projectPaths, packages, propsPath, propsFileExists, backupPath, backupTimestamp);
            backupsCreated = backupEntries.Count > 0;

            var conflicts = VersionResolver.DetectConflicts(packages, existingPackages);

            // CRITICAL: Write manifest BEFORE conflict resolution so rollback can work if resolution fails
            await WriteBackupManifestAsync(options, backupEntries, backupPath, propsPath, propsFileExisted, backupTimestamp);

            var conflictError = await HandleConflictsWithRollbackAsync(options, packages, conflicts, backupsCreated, backupPath);
            if (conflictError != null)
            {
                return conflictError;
            }

            var propsFilePath = await GeneratePropsFileAsync(options, packages);

            await FinalizeMigrationAsync(options, backupPath, propsFilePath, projectPaths.Count, packages.Count, conflicts.Count);

            return new MigrationResult
            {
                ProjectsProcessed = projectPaths.Count,
                PackagesCentralized = packages.Count,
                ConflictsResolved = conflicts.Count,
                PropsFilePath = propsFilePath,
                BackupPath = backupPath,
                WasDryRun = options.DryRun,
                ExitCode = ExitCodes.Success
            };
        }
        catch (Exception ex)
        {
            await HandleMigrationErrorAsync(ex, options, backupsCreated, options.DryRun, backupPath);
            throw; // Re-throw to be handled by Program.cs or caller
        }
    }

    private async Task<MigrationResult?> ValidateMigrationPrerequisitesAsync(
        Options options,
        string outputPath,
        bool propsFileExists,
        string propsPath)
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

    private (bool Success, Dictionary<string, HashSet<string>> Packages, Dictionary<string, HashSet<string>> ExistingPackages, bool PropsFileExisted) LoadPackageState(
        Options options,
        string propsPath,
        bool propsFileExists)
    {
        Dictionary<string, HashSet<string>> packages = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> existingPackages = new(StringComparer.OrdinalIgnoreCase);
        bool hadConditionalPackageVersions = false;

        if (propsFileExists && options.MergeExisting)
        {
            // If merging is requested but we can't parse the existing file, we must abort
            if (!TryLoadExistingPropsPackages(propsPath, existingPackages, out var existingCount, out hadConditionalPackageVersions))
            {
                return (false, packages, existingPackages, propsFileExists);
            }

            foreach (var kvp in existingPackages)
            {
                packages.Add(kvp.Key, new HashSet<string>(kvp.Value));
            }

            if (!_quietMode)
            {
                _consoleService.Info($"Loaded {existingCount} package(s) from existing Directory.Packages.props.");
            }

            if (hadConditionalPackageVersions && !_quietMode)
            {
                _consoleService.Warning("Conditional PackageVersion entries detected; merge will normalize versions.");
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
        string? backupTimestamp)
    {
        List<BackupEntry> backupEntries = [];
        var propsBackupEntry = CreatePropsBackup(options, propsFileExists, propsPath, backupPath, backupTimestamp);

        if (propsBackupEntry != null)
        {
            backupEntries.Add(propsBackupEntry);
        }

        var projectBackups = await ProcessProjectsWithProgressAsync(options, projectPaths, packages, backupPath, backupTimestamp);
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
        out bool hasConditionalPackageVersions)
    {
        existingPackageCount = 0;
        hasConditionalPackageVersions = false;

        try
        {
            var existingPackages = PropsGenerator.ReadExistingPackageVersions(
                propsFilePath, out hasConditionalPackageVersions);
            existingPackageCount = existingPackages.Count;

            foreach (var kvp in existingPackages)
            {
                packages.Add(kvp.Key, new HashSet<string>(kvp.Value));
            }

            return true;
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Failed to read existing Directory.Packages.props: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Discovers projects with a spinner or silently in quiet mode.
    /// </summary>
    private async Task<(string BasePath, List<string> ProjectPaths)> DiscoverProjectsWithSpinnerAsync(Options options)
    {
        if (_quietMode)
        {
            return DiscoverProjects(options);
        }

        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Discovering projects...", async ctx =>
            {
                await Task.Delay(100);
                return DiscoverProjects(options);
            });
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
        List<string> conflicts)
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
            _consoleService.WriteMarkup("[dim]Resolve the conflicts manually or use --conflict-strategy Highest|Lowest.[/]\n");
        }
        return new MigrationResult { ExitCode = ExitCodes.VersionConflict };
    }

    private void ResolveConflictsInteractively(
        Options options,
        Dictionary<string, HashSet<string>> packages,
        List<string> conflicts)
    {
        // --interactive-conflicts on a terminal that cannot prompt would throw on the first
        // package. Fall back to the configured --conflict-strategy — the same deterministic
        // resolution the run would have used without the flag — and say so once, rather than
        // failing a migration that has a perfectly good automatic answer.
        if (!_consoleService.IsInteractive)
        {
            _consoleService.Warning(
                $"--interactive-conflicts needs a prompt-capable terminal; resolving {conflicts.Count} conflict(s) with --conflict-strategy {options.ConflictStrategy} instead.");
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
        var usageCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

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
        string version)
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
        Dictionary<string, Dictionary<string, int>> usageCounts)
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
        Dictionary<string, Dictionary<string, int>> usageCounts)
    {
        return versions.Select(v =>
        {
            var count = GetVersionUsageCount(usageCounts, packageName, v);
            var label = $"{v} (Used by {count} project{(count == 1 ? "" : "s")})";

            if (v == recommended)
            {
                label += " [springgreen1]**Recommended**[/]";
            }

            return label;
        }).ToList();
    }

    private static int GetVersionUsageCount(
        Dictionary<string, Dictionary<string, int>> usageCounts,
        string packageName,
        string version)
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
        string? backupTimestamp) =>
        await _backupCoordinator.WriteManifestAsync(
            MigrationRequest.FromOptions(options),
            backupEntries,
            backupPath,
            propsFilePath,
            propsFileExisted,
            backupTimestamp);

    /// <summary>
    /// Manages .gitignore for the backup directory.
    /// </summary>
    private async Task ManageGitIgnoreAsync(Options options, string? backupPath) =>
        await _backupCoordinator.ManageGitIgnoreAsync(MigrationRequest.FromOptions(options), backupPath);


    private (string BasePath, List<string> ProjectPaths) DiscoverProjects(Options options)
    {
        if (options.HasExplicitProjectPath)
        {
            return _projectAnalyzer.DiscoverProjectFromPath(options.ProjectFileDir);
        }

        return _projectAnalyzer.DiscoverProjectsFromSolution(options.GetDiscoveryTargetPath());
    }

    private async Task<List<BackupEntry>> ProcessProjectsWithProgressAsync(Options options, List<string> projectPaths,
        Dictionary<string, HashSet<string>> packages, string? backupPath, string? backupTimestamp)
    {
        List<BackupEntry> backupEntries = [];

        // Process without progress bar in quiet mode
        if (_quietMode)
        {
            foreach (var projectFilePath in projectPaths)
            {
                var backupEntry = await ProcessSingleProjectAsync(
                    options, projectFilePath, packages, backupPath, backupTimestamp, null);
                if (backupEntry != null)
                {
                    backupEntries.Add(backupEntry);
                }
            }
            return backupEntries;
        }

        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Processing projects[/]", maxValue: projectPaths.Count);

                foreach (var projectFilePath in projectPaths)
                {
                    var projectName = Path.GetFileName(projectFilePath);
                    task.Description = $"[cyan]Processing[/] [white]{Markup.Escape(projectName)}[/]";

                    var backupEntry = await ProcessSingleProjectAsync(
                        options, projectFilePath, packages, backupPath, backupTimestamp, task);
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

    private async Task<string> GeneratePropsFileAsync(Options options,
        Dictionary<string, HashSet<string>> packages)
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
        Dictionary<string, HashSet<string>> packages)
    {
        var (mergedContent, addedCount, updatedCount, _) = _propsGenerator.MergeExisting(
            propsFilePath, packages, options.ConflictStrategy);

        if (options.DryRun)
        {
            if (!_quietMode)
            {
                _consoleService.WriteLine();
                _consoleService.DryRun($"Would update: {propsFilePath}");
                _consoleService.WriteLine();
                _consoleService.WritePropsPreview(mergedContent);
            }
        }
        else
        {
            await FileHelper.WriteAtomicAsync(propsFilePath, mergedContent);
            if (!_quietMode)
            {
                _consoleService.WriteMarkup($"\n[green]:page_facing_up: Updated:[/] [cyan]{Markup.Escape(propsFilePath)}[/]\n");
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
        Dictionary<string, HashSet<string>> packages)
    {
        var updatedPackagePropsContent = _propsGenerator.Generate(packages, options.ConflictStrategy);

        if (options.DryRun)
        {
            if (!_quietMode)
            {
                _consoleService.WriteLine();
                _consoleService.DryRun($"Would create: {propsFilePath}");
                _consoleService.WriteLine();
                _consoleService.WritePropsPreview(updatedPackagePropsContent);
            }
        }
        else
        {
            await FileHelper.WriteAtomicAsync(propsFilePath, updatedPackagePropsContent);
            if (!_quietMode)
            {
                _consoleService.WriteMarkup($"\n[green]:page_facing_up: Generated:[/] [cyan]{Markup.Escape(propsFilePath)}[/]\n");
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
        string? backupTimestamp) =>
        _backupCoordinator.CreatePropsBackup(
            MigrationRequest.FromOptions(options),
            propsFileExists,
            propsPath,
            backupPath,
            backupTimestamp);

    /// <summary>
    /// Handles version conflicts and offers rollback if migration has already modified files.
    /// </summary>
    private async Task<MigrationResult?> HandleConflictsWithRollbackAsync(
        Options options,
        Dictionary<string, HashSet<string>> packages,
        List<string> conflicts,
        bool backupsCreated,
        string? backupPath)
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
            _consoleService.Warning("Migration interrupted during conflict resolution. Project files have already been modified.");

            if (backupsCreated && !string.IsNullOrEmpty(backupPath))
            {
                if (ShouldProceedWithAutomaticRollback(options, "Would you like to rollback changes using the created backup?"))
                {
                    await _rollbackHandler.ExecuteAsync(CreateRollbackOptions(options, backupPath));
                }
            }
            else
            {
                _consoleService.Info("Note: No backups were created (or backup was disabled), so automatic rollback is unavailable.");
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
        int conflictCount)
    {
        // Manifest already written before conflict resolution
        await ManageGitIgnoreAsync(options, backupPath);

        if (!_quietMode)
        {
            _display.ShowMigrationSummary(projectCount, packageCount, conflictCount, propsFilePath, options.DryRun);
            _display.ShowPostMigrationGuidance(options, propsFilePath);
        }
    }

    /// <summary>
    /// Handles migration errors and offers automatic rollback if backups exist.
    /// </summary>
    private async Task HandleMigrationErrorAsync(Exception ex, Options options, bool backupsCreated, bool dryRun, string? backupPath)
    {
        _consoleService.Error($"\nAn error occurred during migration: {ex.Message}");

        if (backupsCreated && !dryRun && !string.IsNullOrEmpty(backupPath))
        {
            _consoleService.Warning("Project files may have been partially modified.");
            if (ShouldProceedWithAutomaticRollback(options, "Would you like to attempt an automatic rollback to the last backup?"))
            {
                await _rollbackHandler.ExecuteAsync(CreateRollbackOptions(options, backupPath));
            }
        }
    }

    private bool ShouldProceedWithAutomaticRollback(Options options, string prompt)
    {
        if (options.Force)
        {
            return true;
        }

        if (options.Output == OutputFormat.Json)
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
            _consoleService.Warning("Migration failed; rolling back automatically (no prompt available on this terminal).");
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
            Quiet = sourceOptions.Quiet
        };
    }

    private static string ResolveRollbackBackupDir(string backupPath, string fallbackBackupDir)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return fallbackBackupDir;
        }

        var normalizedPath = backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(normalizedPath), ".cpmigrate_backup", StringComparison.OrdinalIgnoreCase))
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
        Spectre.Console.ProgressTask? progressTask)
    {
        BackupEntry? backupEntry = null;

        // Create backup
        if (!options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath))
        {
            backupEntry = _backupManager.CreateBackupForProject(
                options, projectFilePath, backupPath, backupTimestamp);
        }

        // Cache scan results before processing (for use by interactive conflict resolution)
        var (scannedRefs, _) = _projectAnalyzer.ScanProjectPackages(projectFilePath);
        _cachedProjectScans ??= new Dictionary<string, List<PackageReference>>(StringComparer.OrdinalIgnoreCase);
        _cachedProjectScans[projectFilePath] = scannedRefs;

        // Process project file
        var projectFileContent = ProjectAnalyzer.ProcessProject(
            projectFilePath, packages, options.KeepAttributes);

        // Handle transitive dependencies if requested
        if (options.IncludeTransitive)
        {
            if (progressTask != null)
            {
                var projectName = Path.GetFileName(projectFilePath);
                progressTask.Description = $"[cyan]Scanning transitive[/] [white]{Markup.Escape(projectName)}[/]";
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
    private async Task AddTransitivePackagesAsync(string projectFilePath, Dictionary<string, HashSet<string>> packages)
    {
        var (transitiveRefs, transitiveSuccess) = await _projectAnalyzer.ScanTransitivePackagesAsync(projectFilePath);
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
