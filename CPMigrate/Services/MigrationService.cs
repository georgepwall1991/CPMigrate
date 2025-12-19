using CPMigrate.Analyzers;
using CPMigrate.Fixers;
using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates the CPM migration process.
/// </summary>
public class MigrationService
{
    private readonly ProjectAnalyzer _projectAnalyzer;
    private readonly VersionResolver _versionResolver;
    private readonly PropsGenerator _propsGenerator;
    private readonly BackupManager _backupManager;
    private readonly IConsoleService _consoleService;
    private readonly AnalysisService _analysisService;
    private readonly FixService _fixService;
    private readonly bool _quietMode;

    public MigrationService(
        IConsoleService consoleService,
        ProjectAnalyzer? projectAnalyzer = null,
        VersionResolver? versionResolver = null,
        PropsGenerator? propsGenerator = null,
        BackupManager? backupManager = null,
        AnalysisService? analysisService = null,
        bool quietMode = false)
    {
        _consoleService = consoleService;
        _versionResolver = versionResolver ?? new VersionResolver(_consoleService);
        _projectAnalyzer = projectAnalyzer ?? new ProjectAnalyzer(_consoleService);
        _propsGenerator = propsGenerator ?? new PropsGenerator(_versionResolver);
        _backupManager = backupManager ?? new BackupManager();
        _analysisService = analysisService ?? new AnalysisService();
        _fixService = new FixService(_consoleService);
        _quietMode = quietMode;
    }

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

        if (!TryValidateOptions(options, out var validationError))
        {
            return validationError;
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
        var (outputPath, propsPath) = GetOutputPaths(options);

        if (!options.DryRun)
        {
            var directoryError = await ValidateOutputDirectoryAsync(outputPath);
            if (directoryError != null)
            {
                return directoryError;
            }
        }

        if (IsAlreadyMigrated(propsPath))
        {
            return CreateAlreadyMigratedResult(propsPath);
        }

        ShowDryRunBannerIfNeeded(options);

        var (basePath, projectPaths) = await DiscoverProjectsWithSpinnerAsync(options);
        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to process.");
            return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
        }

        ShowDiscoveredProjects(basePath, projectPaths);

        var packages = new Dictionary<string, HashSet<string>>();
        var backupPath = SetupBackupDirectory(options);

        var backupEntries = await ProcessProjectsWithProgressAsync(options, projectPaths, packages, backupPath);

        var conflicts = _versionResolver.DetectConflicts(packages);
        var conflictError = HandleVersionConflicts(options, packages, conflicts);
        if (conflictError != null)
        {
            return conflictError;
        }

        var propsFilePath = await GeneratePropsFileAsync(options, packages);

        await WriteBackupManifestAsync(options, backupEntries, backupPath, propsFilePath);
        await ManageGitIgnoreAsync(options, backupPath);

        ShowMigrationSummary(options, projectPaths.Count, packages.Count, conflicts.Count, propsFilePath, backupPath);

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

    /// <summary>
    /// Validates options and returns false with an error result if validation fails.
    /// </summary>
    private bool TryValidateOptions(Options options, out MigrationResult errorResult)
    {
        try
        {
            options.Validate();
            errorResult = null!;
            return true;
        }
        catch (ArgumentException ex)
        {
            _consoleService.Error(ex.Message);
            errorResult = new MigrationResult { ExitCode = ExitCodes.ValidationError };
            return false;
        }
    }

    /// <summary>
    /// Gets the output directory and props file paths.
    /// </summary>
    private static (string OutputPath, string PropsPath) GetOutputPaths(Options options)
    {
        var outputDir = string.IsNullOrEmpty(options.OutputDir) ? "." : options.OutputDir;
        var outputPath = Path.GetFullPath(outputDir);
        var propsPath = Path.Combine(outputPath, "Directory.Packages.props");
        return (outputPath, propsPath);
    }

    /// <summary>
    /// Validates that the output directory exists and is writable.
    /// </summary>
    private async Task<MigrationResult?> ValidateOutputDirectoryAsync(string outputPath)
    {
        if (!Directory.Exists(outputPath))
        {
            _consoleService.Error($"Output directory does not exist: {outputPath}");
            return new MigrationResult { ExitCode = ExitCodes.ValidationError };
        }

        var testFile = Path.Combine(outputPath, $".cpmigrate_test_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(testFile, "test");
            return null;
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Cannot write to output directory: {outputPath}");
            _consoleService.Dim($"Error: {ex.Message}");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }
        finally
        {
            try { if (File.Exists(testFile)) File.Delete(testFile); } catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Checks if the solution has already been migrated to CPM.
    /// </summary>
    private static bool IsAlreadyMigrated(string propsPath) => File.Exists(propsPath);

    /// <summary>
    /// Creates a result indicating the solution is already migrated.
    /// </summary>
    private MigrationResult CreateAlreadyMigratedResult(string propsPath)
    {
        _consoleService.Warning("This solution has already been migrated to Central Package Management.");
        _consoleService.WriteMarkup($"[dim]Found existing:[/] [cyan]{Markup.Escape(propsPath)}[/]\n");
        _consoleService.WriteLine();
        _consoleService.Info("To re-migrate, first rollback with: cpmigrate --rollback");
        _consoleService.Info("To analyze packages, use: cpmigrate --analyze");
        return new MigrationResult { ExitCode = ExitCodes.Success, PropsFilePath = propsPath };
    }

    /// <summary>
    /// Shows the dry-run banner if in dry-run mode.
    /// </summary>
    private void ShowDryRunBannerIfNeeded(Options options)
    {
        if (options.DryRun && !_quietMode)
        {
            _consoleService.Banner("DRY-RUN MODE - No files will be modified");
            _consoleService.WriteLine();
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
    /// Shows the discovered projects tree.
    /// </summary>
    private void ShowDiscoveredProjects(string basePath, List<string> projectPaths)
    {
        if (_quietMode) return;

        _consoleService.WriteMarkup($"\n[green]:magnifying_glass_tilted_right: Found {projectPaths.Count} project(s)[/]\n");

        if (!string.IsNullOrEmpty(basePath))
        {
            _consoleService.WriteProjectTree(projectPaths, basePath);
        }
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

            if (!_quietMode) _consoleService.WriteLine();
            return null;
        }

        var backupPath = _backupManager.CreateBackupDirectory(options);
        if (!string.IsNullOrEmpty(backupPath) && !_quietMode)
        {
            _consoleService.WriteMarkup($"[dim]:file_folder: Backup directory: {Markup.Escape(backupPath)}[/]\n");
        }

        if (!_quietMode) _consoleService.WriteLine();
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
        if (conflicts.Count == 0) return null;

        if (!_quietMode)
        {
            _consoleService.WriteConflictsTable(packages, conflicts, options.ConflictStrategy);
        }

        if (options.ConflictStrategy == ConflictStrategy.Fail)
        {
            _consoleService.Error("Version conflicts detected and --conflict-strategy is set to Fail.");
            if (!_quietMode)
            {
                _consoleService.WriteMarkup("[dim]Resolve the conflicts manually or use --conflict-strategy Highest|Lowest.[/]\n");
            }
            return new MigrationResult { ExitCode = ExitCodes.VersionConflict };
        }

        // Interactive conflict resolution
        if (options.InteractiveConflicts)
        {
            _consoleService.WriteLine();
            _consoleService.Banner("INTERACTIVE CONFLICT RESOLUTION");
            _consoleService.WriteLine();
            _consoleService.Info("Select the version to use for each package with conflicts:");
            _consoleService.WriteLine();

            foreach (var packageName in conflicts)
            {
                if (!packages.TryGetValue(packageName, out var versions)) continue;

                var versionList = versions.OrderByDescending(v => v).ToList();
                var recommended = _versionResolver.ResolveVersion(versions, options.ConflictStrategy);
                var choices = versionList.Select(v =>
                    v == recommended ? $"{v} (recommended)" : v).ToList();

                var selected = _consoleService.AskSelection($"Version for {packageName}?", choices);
                var selectedVersion = selected.Replace(" (recommended)", "");

                // Update packages to only contain selected version
                packages[packageName] = new HashSet<string> { selectedVersion };
            }

            _consoleService.WriteLine();
            _consoleService.Success("All conflicts resolved interactively.");
        }

        return null;
    }

    /// <summary>
    /// Writes the backup manifest if backups were created.
    /// </summary>
    private async Task WriteBackupManifestAsync(
        Options options,
        List<BackupEntry> backupEntries,
        string? backupPath,
        string propsFilePath)
    {
        if (options.DryRun || options.NoBackup || backupEntries.Count == 0 || string.IsNullOrEmpty(backupPath))
        {
            return;
        }

        var manifest = new BackupManifest
        {
            Timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssZ"),
            PropsFilePath = propsFilePath,
            Backups = backupEntries
        };
        await _backupManager.WriteManifestAsync(backupPath, manifest);
    }

    /// <summary>
    /// Manages .gitignore for the backup directory.
    /// </summary>
    private async Task ManageGitIgnoreAsync(Options options, string? backupPath)
    {
        if (!options.DryRun)
        {
            await _backupManager.ManageGitIgnore(options, backupPath);
        }
        else if (options.AddBackupToGitignore && !options.NoBackup && !_quietMode)
        {
            _consoleService.DryRun("Would add backup directory to .gitignore");
        }
    }

    /// <summary>
    /// Shows the migration summary table.
    /// </summary>
    private void ShowMigrationSummary(
        Options options,
        int projectCount,
        int packageCount,
        int conflictCount,
        string propsFilePath,
        string? backupPath)
    {
        if (_quietMode) return;

        _consoleService.WriteSummaryTable(
            projectCount,
            packageCount,
            conflictCount,
            propsFilePath,
            backupPath,
            options.DryRun);
    }

    private (string BasePath, List<string> ProjectPaths) DiscoverProjects(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.SolutionFileDir))
        {
            return _projectAnalyzer.DiscoverProjectsFromSolution(options.SolutionFileDir);
        }

        if (!string.IsNullOrWhiteSpace(options.ProjectFileDir))
        {
            return _projectAnalyzer.DiscoverProjectFromPath(options.ProjectFileDir);
        }

        _consoleService.Error("Either solution (-s) or project (-p) path must be specified.");
        return (string.Empty, new List<string>());
    }

    private async Task<List<BackupEntry>> ProcessProjectsWithProgressAsync(Options options, List<string> projectPaths,
        Dictionary<string, HashSet<string>> packages, string? backupPath)
    {
        var backupEntries = new List<BackupEntry>();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssZ");

        // Process without progress bar in quiet mode
        if (_quietMode)
        {
            foreach (var projectFilePath in projectPaths)
            {
                var projectName = Path.GetFileName(projectFilePath);

                // Backup
                if (!options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath))
                {
                    var backupFileName = $"{projectName}.backup_{timestamp}";
                    _backupManager.CreateBackupForProject(options, projectFilePath, backupPath);

                    backupEntries.Add(new BackupEntry
                    {
                        OriginalPath = Path.GetFullPath(projectFilePath),
                        BackupFileName = backupFileName
                    });
                }

                // Process project file
                var projectFileContent = _projectAnalyzer.ProcessProject(
                    projectFilePath, packages, options.KeepAttributes);

                if (!options.DryRun)
                {
                    await File.WriteAllTextAsync(projectFilePath, projectFileContent);
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

                    // Backup
                    if (!options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath))
                    {
                        var backupFileName = $"{projectName}.backup_{timestamp}";
                        _backupManager.CreateBackupForProject(options, projectFilePath, backupPath);

                        backupEntries.Add(new BackupEntry
                        {
                            OriginalPath = Path.GetFullPath(projectFilePath),
                            BackupFileName = backupFileName
                        });
                    }

                    // Process project file
                    var projectFileContent = _projectAnalyzer.ProcessProject(
                        projectFilePath, packages, options.KeepAttributes);

                    if (!options.DryRun)
                    {
                        await File.WriteAllTextAsync(projectFilePath, projectFileContent);
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
        var updatedPackagePropsContent = _propsGenerator.Generate(packages, options.ConflictStrategy);
        var outputDir = string.IsNullOrEmpty(options.OutputDir) ? "." : options.OutputDir;
        var outputPath = Path.GetFullPath(outputDir);
        var propsFilePath = Path.Combine(outputPath, "Directory.Packages.props");

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
            await File.WriteAllTextAsync(propsFilePath, updatedPackagePropsContent);
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
        _consoleService.Banner("ROLLBACK MODE - Restoring from backup");
        _consoleService.WriteLine();

        var backupPath = _backupManager.GetBackupDirectoryPath(options);

        // Check if backup directory exists
        if (!Directory.Exists(backupPath))
        {
            _consoleService.Error($"No backup directory found at: {backupPath}");
            _consoleService.WriteMarkup("[dim]Run a migration first to create backups.[/]\n");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }

        // Read manifest
        var manifest = await _backupManager.ReadManifestAsync(backupPath);
        if (manifest == null)
        {
            _consoleService.Error("No backup manifest found or manifest is corrupted.");
            _consoleService.WriteMarkup("[dim]Cannot determine which files to restore.[/]\n");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }

        if (manifest.Backups.Count == 0)
        {
            _consoleService.Warning("No backup entries found in manifest - nothing to restore.");
            _consoleService.Dim("The backup manifest exists but contains no files. This may indicate:");
            _consoleService.Dim("  - A previous rollback already completed");
            _consoleService.Dim("  - The migration was run with --no-backup");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        // Show preview
        var filesToRestore = manifest.Backups.Select(b => b.OriginalPath).ToList();
        _consoleService.WriteRollbackPreview(filesToRestore, manifest.PropsFilePath);

        // Ask for confirmation
        if (!_consoleService.AskConfirmation("Proceed with rollback?"))
        {
            _consoleService.Info("Rollback cancelled.");
            return new MigrationResult { ExitCode = ExitCodes.Success };
        }

        _consoleService.WriteLine();

        // Restore files with progress
        var restoredCount = 0;
        var failedCount = 0;

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

                    try
                    {
                        _backupManager.RestoreFile(backupPath, entry);
                        restoredCount++;
                    }
                    catch (Exception ex)
                    {
                        _consoleService.Error($"Failed to restore {fileName}: {ex.Message}");
                        failedCount++;
                    }

                    task.Increment(1);
                    await Task.Delay(50);
                }

                task.Description = "[green]Restore complete[/]";
            });

        _consoleService.WriteLine();

        // Only delete Directory.Packages.props if ALL files restored successfully
        if (failedCount == 0)
        {
            if (!string.IsNullOrEmpty(manifest.PropsFilePath) && File.Exists(manifest.PropsFilePath))
            {
                try
                {
                    File.Delete(manifest.PropsFilePath);
                    _consoleService.Success($"Deleted: {manifest.PropsFilePath}");
                }
                catch (Exception ex)
                {
                    _consoleService.Warning($"Could not delete props file: {ex.Message}");
                }
            }

            // Clean up backups only on full success
            var cleanupErrors = _backupManager.CleanupBackups(backupPath, manifest);
            if (cleanupErrors.Count == 0)
            {
                _consoleService.Dim("Cleaned up backup files.");
            }
            else
            {
                _consoleService.Warning($"Cleanup completed with {cleanupErrors.Count} error(s):");
                foreach (var error in cleanupErrors)
                {
                    _consoleService.Dim($"  - {error}");
                }
            }
        }
        else
        {
            _consoleService.Warning("Props file NOT deleted due to restore failures.");
            _consoleService.Dim("Backup files retained for manual recovery.");
        }

        // Summary
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

        return new MigrationResult
        {
            ProjectsProcessed = restoredCount,
            ExitCode = failedCount == 0 ? ExitCodes.Success : ExitCodes.FileOperationError
        };
    }

    /// <summary>
    /// Lists all available backups with timestamps and file counts.
    /// </summary>
    /// <param name="options">Options containing backup directory path.</param>
    /// <returns>Migration result with exit code.</returns>
    private async Task<MigrationResult> ExecuteListBackupsAsync(Options options)
    {
        _consoleService.Banner("BACKUP HISTORY");
        _consoleService.WriteLine();

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

        var index = 1;
        long totalSize = 0;
        int totalFiles = 0;

        foreach (var backup in backups)
        {
            // Parse timestamp (format: yyyyMMddHHmmss)
            var displayTime = "Unknown";
            if (backup.Timestamp.Length == 14 &&
                DateTime.TryParseExact(backup.Timestamp, "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dateTime))
            {
                displayTime = dateTime.ToString("yyyy-MM-dd HH:mm:ss");
            }

            // Calculate size
            long backupSize = 0;
            foreach (var file in backup.Files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    backupSize += fileInfo.Length;
                }
                catch { /* Ignore errors */ }
            }
            totalSize += backupSize;
            totalFiles += backup.Files.Count;

            var sizeStr = FormatFileSize(backupSize);
            var isNewest = index == 1;
            var rowStyle = isNewest ? "[green]" : "[white]";

            table.AddRow(
                $"{rowStyle}{index}[/]",
                $"{rowStyle}{backup.Timestamp}[/]",
                $"{rowStyle}{displayTime}[/]",
                $"{rowStyle}{backup.Files.Count}[/]",
                $"{rowStyle}{sizeStr}[/]"
            );
            index++;
        }

        AnsiConsole.Write(table);
        _consoleService.WriteLine();

        _consoleService.Info($"Total: {backups.Count} backup set(s), {totalFiles} file(s), {FormatFileSize(totalSize)}");
        _consoleService.Dim($"Backup directory: {backupPath}");

        await Task.CompletedTask;
        return new MigrationResult { ExitCode = ExitCodes.Success };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    /// <summary>
    /// Executes package analysis without modifying files.
    /// </summary>
    /// <param name="options">Options containing project/solution path.</param>
    /// <returns>Migration result with exit code based on issues found.</returns>
    private async Task<MigrationResult> ExecuteAnalysisAsync(Options options)
    {
        _consoleService.Banner("ANALYZE MODE - Scanning for package issues");
        _consoleService.WriteLine();

        // Discover projects
        var (basePath, projectPaths) = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Discovering projects...", async ctx =>
            {
                await Task.Delay(100);
                return DiscoverProjects(options);
            });

        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to analyze.");
            return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
        }

        // Scan all packages
        var allReferences = new List<PackageReference>();
        var scanFailures = 0;

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

                    var (references, success) = _projectAnalyzer.ScanProjectPackages(projectPath);
                    allReferences.AddRange(references);
                    if (!success) scanFailures++;

                    task.Increment(1);
                    await Task.Delay(30);
                }

                task.Description = "[green]Scan complete[/]";
            });

        _consoleService.WriteLine();

        // Warn if significant number of projects failed to scan
        if (scanFailures > 0)
        {
            var failureRate = (double)scanFailures / projectPaths.Count * 100;
            _consoleService.Warning($"{scanFailures} of {projectPaths.Count} projects ({failureRate:F0}%) failed to scan.");
            if (failureRate > 50)
            {
                _consoleService.Warning("High failure rate detected - analysis results may be incomplete.");
            }
        }

        var packageInfo = new ProjectPackageInfo(allReferences);

        // Write header
        _consoleService.WriteAnalysisHeader(packageInfo.ProjectCount, packageInfo.TotalReferences);

        // Run analysis
        var report = _analysisService.Analyze(packageInfo);

        // Write results for each analyzer
        foreach (var result in report.Results)
        {
            _consoleService.WriteAnalyzerResult(result);
        }

        // Write summary
        _consoleService.WriteAnalysisSummary(report);

        // Apply fixes if requested
        if ((options.Fix || options.FixDryRun) && report.HasIssues)
        {
            _consoleService.WriteLine();
            _consoleService.Banner(options.FixDryRun ? "FIX DRY RUN - Showing proposed changes" : "APPLYING FIXES");
            _consoleService.WriteLine();

            var fixReport = _fixService.ApplyFixes(report, packageInfo, options, options.FixDryRun);

            // Adjust exit code based on fix results
            if (fixReport.HasChanges && !options.FixDryRun)
            {
                // Fixes were applied successfully
                return new MigrationResult
                {
                    ProjectsProcessed = packageInfo.ProjectCount,
                    PackagesCentralized = packageInfo.TotalReferences,
                    ExitCode = fixReport.FailedFixes.Count > 0
                        ? ExitCodes.AnalysisIssuesFound  // Some fixes failed
                        : ExitCodes.Success              // All fixes succeeded
                };
            }
        }

        return new MigrationResult
        {
            ProjectsProcessed = packageInfo.ProjectCount,
            PackagesCentralized = packageInfo.TotalReferences,
            ExitCode = report.HasIssues ? ExitCodes.AnalysisIssuesFound : ExitCodes.Success
        };
    }
}