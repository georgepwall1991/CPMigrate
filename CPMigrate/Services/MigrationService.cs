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
    private readonly IAnalysisService _analysisService;
    private readonly IFixService _fixService;
    private readonly MigrationValidator _validator;
    private readonly MigrationDisplay _display;
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
        _analysisService = analysisService ?? CreateDefaultAnalysisService(consoleService);
        _fixService = fixService ?? CreateDefaultFixService(consoleService, _versionResolver);
        _validator = new MigrationValidator(consoleService);
        _display = new MigrationDisplay(consoleService);
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
            return await ExecuteRollbackAsync(options);
        }

        if (options.ListBackups)
        {
            return await ExecuteListBackupsAsync(options);
        }

        if (options.Analyze)
        {
            return await ExecuteAnalysisAsync(options);
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
    private string? SetupBackupDirectory(Options options)
    {
        if (options.DryRun)
        {
            if (!options.NoBackup && !_quietMode)
            {
                var potentialBackupPath = Path.Combine(
                    Path.GetFullPath(string.IsNullOrEmpty(options.BackupDir) ? "." : options.BackupDir),
                    ".cpmigrate_backup");
                _consoleService.DryRun($"Would create backup directory: {potentialBackupPath}");
            }

            if (!_quietMode)
            {
                _consoleService.WriteLine();
            }

            return null;
        }

        var backupPath = BackupManager.CreateBackupDirectory(options);
        if (!string.IsNullOrEmpty(backupPath) && !_quietMode)
        {
            _consoleService.WriteMarkup($"[dim]:file_folder: Backup directory: {Markup.Escape(backupPath)}[/]\n");
        }

        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }

        return backupPath;
    }

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
        string? backupTimestamp)
    {
        if (options.DryRun || options.NoBackup || backupEntries.Count == 0 || string.IsNullOrEmpty(backupPath))
        {
            return;
        }

        var manifestTimestamp = backupTimestamp ?? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var manifest = new BackupManifest
        {
            Timestamp = manifestTimestamp,
            PropsFilePath = propsFilePath,
            PropsFileExisted = propsFileExisted,
            Backups = backupEntries
        };
        await BackupManager.WriteManifestAsync(backupPath, manifest);
    }

    /// <summary>
    /// Manages .gitignore for the backup directory.
    /// </summary>
    private async Task ManageGitIgnoreAsync(Options options, string? backupPath)
    {
        if (!options.DryRun)
        {
            await BackupManager.ManageGitIgnore(options, backupPath);
        }
        else if (options.AddBackupToGitignore && !options.NoBackup && !_quietMode)
        {
            _consoleService.DryRun("Would add backup directory to .gitignore");
        }
    }


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
    /// Executes rollback to restore project files from backup.
    /// </summary>
    /// <param name="options">Options containing backup directory path.</param>
    /// <returns>Migration result with exit code.</returns>
    private async Task<MigrationResult> ExecuteRollbackAsync(Options options)
    {
        if (!_quietMode)
        {
            _consoleService.Banner("ROLLBACK MODE - Restoring from backup");
            _consoleService.WriteLine();
        }

        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        var validationError = ValidateRollbackPrerequisites(backupPath);
        if (validationError != null)
        {
            return validationError;
        }

        var manifest = await BackupManager.ReadManifestAsync(backupPath);
        if (manifest == null || manifest.Backups.Count == 0)
        {
            return HandleEmptyOrMissingManifest(manifest);
        }

        if (!ShowPreviewAndConfirm(options, manifest))
        {
            _consoleService.Info("Rollback cancelled.");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        _consoleService.WriteLine();

        var (restoredCount, failedCount) = await RestoreFilesWithProgress(backupPath, manifest);
        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }

        HandlePostRestoreCleanup(backupPath, manifest, failedCount);
        ShowRollbackSummary(restoredCount, failedCount);

        return new MigrationResult
        {
            ProjectsProcessed = restoredCount,
            ExitCode = failedCount == 0 ? ExitCodes.Success : ExitCodes.FileOperationError
        };
    }

    private MigrationResult? ValidateRollbackPrerequisites(string backupPath)
    {
        if (!Directory.Exists(backupPath))
        {
            _consoleService.Error($"No backup directory found at: {backupPath}");
            _consoleService.WriteMarkup("[dim]Run a migration first to create backups.[/]\n");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }

        return null;
    }

    private MigrationResult HandleEmptyOrMissingManifest(BackupManifest? manifest)
    {
        if (manifest == null)
        {
            _consoleService.Error("No backup manifest found or manifest is corrupted.");
            _consoleService.WriteMarkup("[dim]Cannot determine which files to restore.[/]\n");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }

        _consoleService.Warning("No backup entries found in manifest - nothing to restore.");
        _consoleService.Dim("The backup manifest exists but contains no files. This may indicate:");
        _consoleService.Dim("  - A previous rollback already completed");
        _consoleService.Dim("  - The migration was run with --no-backup");
        return new MigrationResult { ExitCode = ExitCodes.Success };
    }

    private bool ShowPreviewAndConfirm(Options options, BackupManifest manifest)
    {
        if (options.Force)
        {
            return true;
        }

        if (_quietMode)
        {
            // In strict JSON mode we never auto-confirm destructive operations.
            if (options.Output == OutputFormat.Json)
            {
                return false;
            }

            return true;
        }

        var filesToRestore = manifest.Backups.Select(b => b.OriginalPath).ToList();
        _consoleService.WriteRollbackPreview(filesToRestore, manifest.PropsFilePath);
        return _consoleService.AskConfirmation("Proceed with rollback?");
    }

    private async Task<(int RestoredCount, int FailedCount)> RestoreFilesWithProgress(
        string backupPath,
        BackupManifest manifest)
    {
        var restoredCount = 0;
        var failedCount = 0;

        if (_quietMode)
        {
            foreach (var entry in manifest.Backups)
            {
                var fileName = Path.GetFileName(entry.OriginalPath);
                if (TryRestoreFile(backupPath, entry, fileName))
                {
                    restoredCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            return (restoredCount, failedCount);
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
                var task = ctx.AddTask("[cyan]Restoring files[/]", maxValue: manifest.Backups.Count);

                foreach (var entry in manifest.Backups)
                {
                    var fileName = Path.GetFileName(entry.OriginalPath);
                    task.Description = $"[cyan]Restoring[/] [white]{Markup.Escape(fileName)}[/]";

                    if (TryRestoreFile(backupPath, entry, fileName))
                    {
                        restoredCount++;
                    }
                    else
                    {
                        failedCount++;
                    }

                    task.Increment(1);
                    await Task.Delay(50);
                }

                task.Description = "[green]Restore complete[/]";
            });

        return (restoredCount, failedCount);
    }

    private bool TryRestoreFile(string backupPath, BackupEntry entry, string fileName)
    {
        try
        {
            BackupManager.RestoreFile(backupPath, entry);
            return true;
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Failed to restore {fileName}: {ex.Message}");
            return false;
        }
    }

    private void HandlePostRestoreCleanup(string backupPath, BackupManifest manifest, int failedCount)
    {
        if (failedCount == 0)
        {
            HandleSuccessfulRestore(backupPath, manifest);
        }
        else
        {
            HandleFailedRestore(manifest);
        }
    }

    private void HandleSuccessfulRestore(string backupPath, BackupManifest manifest)
    {
        DeletePropsFileIfNeeded(manifest.PropsFilePath, manifest.PropsFileExisted);
        CleanupBackupFiles(backupPath, manifest);
    }

    private void DeletePropsFileIfNeeded(string propsFilePath, bool propsFileExisted)
    {
        if (string.IsNullOrEmpty(propsFilePath))
        {
            return;
        }

        if (!propsFileExisted && File.Exists(propsFilePath))
        {
            TryDeletePropsFile(propsFilePath);
        }
        else if (propsFileExisted)
        {
            _consoleService.Dim("Preserved existing Directory.Packages.props.");
        }
    }

    private void TryDeletePropsFile(string propsFilePath)
    {
        try
        {
            File.Delete(propsFilePath);
            _consoleService.Success($"Deleted: {propsFilePath}");
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not delete props file: {ex.Message}");
        }
    }

    private void CleanupBackupFiles(string backupPath, BackupManifest manifest)
    {
        if (string.IsNullOrEmpty(backupPath))
        {
            return;
        }

        var cleanupErrors = BackupManager.CleanupBackups(backupPath, manifest);

        if (cleanupErrors.Count == 0)
        {
            _consoleService.Dim("Cleaned up backup files.");
        }
        else
        {
            ShowCleanupErrors(cleanupErrors);
        }
    }

    private void ShowCleanupErrors(List<string> cleanupErrors)
    {
        _consoleService.Warning($"Cleanup completed with {cleanupErrors.Count} error(s):");
        foreach (var error in cleanupErrors)
        {
            _consoleService.Dim($"  - {error}");
        }
    }

    private void HandleFailedRestore(BackupManifest manifest)
    {
        _consoleService.Warning(manifest.PropsFileExisted
            ? "Existing props file retained due to restore failures."
            : "Props file NOT deleted due to restore failures.");
        _consoleService.Dim("Backup files retained for manual recovery.");
    }

    private void ShowRollbackSummary(int restoredCount, int failedCount)
    {
        if (_quietMode)
        {
            return;
        }

        _consoleService.WriteLine();

        if (failedCount == 0)
        {
            _consoleService.Success($"Rollback complete! Restored {restoredCount} file(s).");
        }
        else
        {
            _consoleService.Warning($"Rollback completed with errors. Restored: {restoredCount}, Failed: {failedCount}");
            _consoleService.Dim("Manual intervention may be required. Check backup directory for original files.");
        }
    }

    /// <summary>
    /// Lists all available backups with timestamps and file counts.
    /// </summary>
    /// <param name="options">Options containing backup directory path.</param>
    /// <returns>Migration result with exit code.</returns>
    private async Task<MigrationResult> ExecuteListBackupsAsync(Options options)
    {
        if (!_quietMode)
        {
            _consoleService.Banner("BACKUP HISTORY");
            _consoleService.WriteLine();
        }

        var backupPath = Path.GetFullPath(options.BackupDir);

        if (!Directory.Exists(backupPath))
        {
            _consoleService.Warning($"Backup directory not found: {backupPath}");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        var backups = _backupManager.GetBackupHistory(backupPath);

        if (backups.Count == 0)
        {
            _consoleService.Info("No backups found.");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        // Display backup table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[cyan]#[/]").Centered())
            .AddColumn(new TableColumn("[cyan]Timestamp[/]"))
            .AddColumn(new TableColumn("[cyan]Date/Time[/]"))
            .AddColumn(new TableColumn("[cyan]Files[/]").RightAligned())
            .AddColumn(new TableColumn("[cyan]Size[/]").RightAligned());

        var (totalSize, totalFiles) = PopulateBackupTable(table, backups);

        AnsiConsole.Write(table);
        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }

        _consoleService.Info($"Total: {backups.Count} backup set(s), {totalFiles} file(s), {FormatFileSize(totalSize)}");
        _consoleService.Dim($"Backup directory: {backupPath}");

        return new MigrationResult { ExitCode = ExitCodes.Success };
    }

    private static (long TotalSize, int TotalFiles) PopulateBackupTable(Table table, List<BackupSetInfo> backups)
    {
        var index = 1;
        long totalSize = 0;
        int totalFiles = 0;

        foreach (var backup in backups)
        {
            var displayTime = ParseBackupTimestamp(backup.Timestamp);
            var backupSize = CalculateBackupSize(backup.Files);
            totalSize += backupSize;
            totalFiles += backup.Files.Count;

            AddBackupTableRow(table, index, backup, displayTime, backupSize);
            index++;
        }

        return (totalSize, totalFiles);
    }

    private static string ParseBackupTimestamp(string timestamp)
    {
        if (DateTime.TryParseExact(timestamp, "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return timestamp;
    }

    private static long CalculateBackupSize(List<string> files)
    {
        return files.Sum(f =>
        {
            try
            {
                return File.Exists(f) ? new FileInfo(f).Length : 0;
            }
            catch
            {
                return 0;
            }
        });
    }

    private static void AddBackupTableRow(Table table, int index, BackupSetInfo backup,
        string displayTime, long backupSize)
    {
        table.AddRow(
            $"[cyan]{index}[/]",
            $"[dim]{backup.Timestamp}[/]",
            $"[white]{displayTime}[/]",
            $"[yellow]{backup.Files.Count}[/]",
            $"[green]{FormatFileSize(backupSize)}[/]"
        );
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    /// <summary>
    /// Executes package analysis without modifying files.
    /// </summary>
    /// <param name="options">Options containing project/solution path.</param>
    /// <returns>Migration result with exit code based on issues found.</returns>
    private async Task<MigrationResult> ExecuteAnalysisAsync(Options options)
    {
        if (!_quietMode)
        {
            _consoleService.Banner("ANALYZE MODE - Scanning for package issues");
            _consoleService.WriteLine();
        }

        var (_, projectPaths) = await DiscoverProjectsWithSpinnerAsync(options);
        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to analyze.");
            return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
        }

        var (packageInfo, scanFailures) = await PerformAnalysisScanAsync(options, projectPaths);

        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }
        ReportScanFailures(scanFailures, projectPaths.Count);

        // Filter out bad data if high failure rate? 
        // Current logic keeps going even with failures, which is fine.

        if (!_quietMode)
        {
            _consoleService.WriteAnalysisHeader(packageInfo.ProjectCount, packageInfo.TotalReferences, packageInfo.VulnerabilityCount);
        }

        var report = _analysisService.Analyze(packageInfo);

        if (!_quietMode)
        {
            foreach (var result in report.Results)
            {
                _consoleService.WriteAnalyzerResult(result);
            }
        }

        if (!_quietMode)
        {
            _consoleService.WriteAnalysisSummary(report);
        }

        return await ApplyAnalysisFixesIfNeededAsync(options, report, packageInfo);
    }

    private async Task<(ProjectPackageInfo PackageInfo, int ScanFailures)> PerformAnalysisScanAsync(
        Options options,
        List<string> projectPaths)
    {
        var allReferences = new List<PackageReference>();
        var allVulnerabilities = new List<VulnerabilityInfo>();
        var allOutdatedPackages = new List<OutdatedPackageInfo>();
        var allDeprecatedPackages = new List<DeprecatedPackageInfo>();
        var scanFailures = 0;

        if (_quietMode)
        {
            foreach (var projectPath in projectPaths)
            {
                var success = await ScanSingleProjectForAnalysisAsync(
                    options,
                    projectPath,
                    null,
                    allReferences,
                    allVulnerabilities,
                    allOutdatedPackages,
                    allDeprecatedPackages);

                if (!success)
                {
                    scanFailures++;
                }
            }
        }
        else
        {
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
                    var task = ctx.AddTask("[cyan]Scanning packages[/]", maxValue: projectPaths.Count);

                    foreach (var projectPath in projectPaths)
                    {
                        var projectName = Path.GetFileName(projectPath);
                        task.Description = $"[cyan]Scanning[/] [white]{Markup.Escape(projectName)}[/]";

                        var success = await ScanSingleProjectForAnalysisAsync(
                            options,
                            projectPath,
                            task,
                            allReferences,
                            allVulnerabilities,
                            allOutdatedPackages,
                            allDeprecatedPackages);

                        if (!success)
                        {
                            scanFailures++;
                        }

                        task.Increment(1);
                        await Task.Delay(30);
                    }

                    task.Description = "[green]Scan complete[/]";
                });
        }

        return (new ProjectPackageInfo(allReferences, allVulnerabilities, allOutdatedPackages, allDeprecatedPackages), scanFailures);
    }

    private async Task<bool> ScanSingleProjectForAnalysisAsync(
        Options options,
        string projectPath,
        ProgressTask? task,
        List<PackageReference> allReferences,
        List<VulnerabilityInfo> allVulnerabilities,
        List<OutdatedPackageInfo> allOutdatedPackages,
        List<DeprecatedPackageInfo> allDeprecatedPackages)
    {
        var (references, success) = await ScanProjectReferencesAsync(options, projectPath);
        allReferences.AddRange(references);
        CacheScanResults(projectPath, references);

        if (options.AuditSecurity || options.AnalyzeOutdated || options.AnalyzeDeprecated)
        {
            await RunDeepScansAsync(options, projectPath, task, allVulnerabilities, allOutdatedPackages, allDeprecatedPackages);
        }

        return success;
    }

    private async Task<(List<PackageReference> References, bool Success)> ScanProjectReferencesAsync(Options options, string projectPath)
    {
        var (references, success) = await _projectAnalyzer.ScanResolvedPackagesAsync(projectPath, options.IncludeTransitive);
        if (!success && !options.IncludeTransitive)
        {
            (references, success) = _projectAnalyzer.ScanProjectPackages(projectPath);
        }

        return (references, success);
    }

    private void CacheScanResults(string projectPath, List<PackageReference> references)
    {
        _cachedProjectScans ??= new Dictionary<string, List<PackageReference>>(StringComparer.OrdinalIgnoreCase);
        _cachedProjectScans[projectPath] = references;
    }

    private async Task RunDeepScansAsync(
        Options options,
        string projectPath,
        ProgressTask? task,
        List<VulnerabilityInfo> allVulnerabilities,
        List<OutdatedPackageInfo> allOutdatedPackages,
        List<DeprecatedPackageInfo> allDeprecatedPackages)
    {
        var projectName = Path.GetFileName(projectPath);
        if (task != null)
        {
            task.Description = $"[cyan]Deep scanning[/] [white]{Markup.Escape(projectName)}[/]";
        }

        if (options.AuditSecurity)
        {
            await ScanVulnerabilitiesAsync(projectPath, allVulnerabilities);
        }

        if (options.AnalyzeOutdated)
        {
            await ScanOutdatedPackagesAsync(options, projectPath, allOutdatedPackages);
        }

        if (options.AnalyzeDeprecated)
        {
            await ScanDeprecatedPackagesAsync(options, projectPath, allDeprecatedPackages);
        }
    }

    private async Task ScanVulnerabilitiesAsync(string projectPath, List<VulnerabilityInfo> allVulnerabilities)
    {
        var (vulnerabilities, auditSuccess) = await _projectAnalyzer.ScanVulnerabilitiesAsync(projectPath);
        if (auditSuccess)
        {
            allVulnerabilities.AddRange(vulnerabilities);
        }
    }

    private async Task ScanOutdatedPackagesAsync(Options options, string projectPath, List<OutdatedPackageInfo> allOutdatedPackages)
    {
        var (outdated, outdatedSuccess) = await _projectAnalyzer.ScanOutdatedPackagesAsync(
            projectPath,
            options.IncludeTransitive,
            options.IncludePrerelease);
        if (outdatedSuccess)
        {
            allOutdatedPackages.AddRange(outdated);
        }
    }

    private async Task ScanDeprecatedPackagesAsync(Options options, string projectPath, List<DeprecatedPackageInfo> allDeprecatedPackages)
    {
        var (deprecated, deprecatedSuccess) = await _projectAnalyzer.ScanDeprecatedPackagesAsync(
            projectPath,
            options.IncludeTransitive,
            options.IncludePrerelease);
        if (deprecatedSuccess)
        {
            allDeprecatedPackages.AddRange(deprecated);
        }
    }

    private void ReportScanFailures(int scanFailures, int totalProjects)
    {
        if (scanFailures > 0)
        {
            var failureRate = (double)scanFailures / totalProjects * 100;
            _consoleService.Warning($"{scanFailures} of {totalProjects} projects ({failureRate:F0}%) failed to scan.");
            if (failureRate > 50)
            {
                _consoleService.Warning("High failure rate detected - analysis results may be incomplete.");
            }
        }
    }

    private async Task<MigrationResult> ApplyAnalysisFixesIfNeededAsync(
        Options options,
        AnalysisReport report,
        ProjectPackageInfo packageInfo)
    {
        FixReport? fixReport = null;

        if ((options.Fix || options.FixDryRun) && report.HasIssues)
        {
            if (!_quietMode)
            {
                _consoleService.WriteLine();
                _consoleService.Banner(options.FixDryRun ? "FIX DRY RUN - Showing proposed changes" : "APPLYING FIXES");
                _consoleService.WriteLine();
            }

            fixReport = _fixService.ApplyFixes(report, packageInfo, options, options.FixDryRun);

            if (fixReport.HasChanges && !options.FixDryRun)
            {
                return new MigrationResult
                {
                    ProjectsProcessed = packageInfo.ProjectCount,
                    PackagesCentralized = packageInfo.TotalReferences,
                    AnalysisReport = report,
                    FixReport = fixReport,
                    ExitCode = fixReport.GetFailedFixes().Count > 0
                        ? ExitCodes.AnalysisIssuesFound
                        : ExitCodes.Success
                };
            }
        }

        return await Task.FromResult(new MigrationResult
        {
            ProjectsProcessed = packageInfo.ProjectCount,
            PackagesCentralized = packageInfo.TotalReferences,
            AnalysisReport = report,
            FixReport = fixReport,
            ExitCode = report.HasIssues ? ExitCodes.AnalysisIssuesFound : ExitCodes.Success
        });
    }

    /// <summary>
    /// Creates a backup of the existing Directory.Packages.props file if needed.
    /// </summary>
    private BackupEntry? CreatePropsBackup(
        Options options,
        bool propsFileExists,
        string propsPath,
        string? backupPath,
        string? backupTimestamp)
    {
        if (!propsFileExists || !options.MergeExisting || options.DryRun || options.NoBackup || string.IsNullOrEmpty(backupPath))
        {
            return null;
        }

        var propsBackupEntry = _backupManager.CreateBackupForProject(options, propsPath, backupPath, backupTimestamp);
        if (propsBackupEntry != null && !_quietMode)
        {
            _consoleService.Dim("Backed up existing Directory.Packages.props.");
        }

        return propsBackupEntry;
    }

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
                    await ExecuteRollbackAsync(CreateRollbackOptions(options, backupPath));
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
                await ExecuteRollbackAsync(CreateRollbackOptions(options, backupPath));
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
