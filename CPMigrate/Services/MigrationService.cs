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

    public MigrationService(
        IConsoleService consoleService,
        ProjectAnalyzer? projectAnalyzer = null,
        VersionResolver? versionResolver = null,
        PropsGenerator? propsGenerator = null,
        BackupManager? backupManager = null)
    {
        _consoleService = consoleService;
        _versionResolver = versionResolver ?? new VersionResolver();
        _projectAnalyzer = projectAnalyzer ?? new ProjectAnalyzer(_consoleService);
        _propsGenerator = propsGenerator ?? new PropsGenerator(_versionResolver);
        _backupManager = backupManager ?? new BackupManager();
    }

    /// <summary>
    /// Executes the CPM migration based on the provided options.
    /// </summary>
    /// <param name="options">Migration options from CLI.</param>
    /// <returns>Migration result with exit code and statistics.</returns>
    public async Task<MigrationResult> ExecuteAsync(Options options)
    {
        // Show header
        _consoleService.WriteHeader();

        // Validate options first
        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            _consoleService.Error(ex.Message);
            return new MigrationResult { ExitCode = ExitCodes.ValidationError };
        }

        // Handle rollback mode
        if (options.Rollback)
        {
            return await ExecuteRollbackAsync(options);
        }

        // Dry-run banner
        if (options.DryRun)
        {
            _consoleService.Banner("DRY-RUN MODE - No files will be modified");
            _consoleService.WriteLine();
        }

        // Discover projects with a spinner
        // Note: Still using static AnsiConsole for complex status/progress as wrapping them is more involved
        // and they are purely UI concerns specific to Spectre.Console.
        // Ideally IConsoleService would return an IStatus/IProgress abstraction, but for now we rely on the implementation.
        var (basePath, projectPaths) = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Discovering projects...", async ctx =>
            {
                await Task.Delay(100); // Small delay for visual effect
                return DiscoverProjects(options);
            });

        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to process.");
            return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
        }

        // Show discovered projects
        _consoleService.WriteMarkup($"\n[green]:magnifying_glass_tilted_right: Found {projectPaths.Count} project(s)[/]\n");

        if (!string.IsNullOrEmpty(basePath))
        {
            _consoleService.WriteProjectTree(projectPaths, basePath);
        }

        var packages = new Dictionary<string, HashSet<string>>();

        // Create backup directory
        string? backupPath = null;
        if (!options.DryRun)
        {
            backupPath = _backupManager.CreateBackupDirectory(options);
            if (!string.IsNullOrEmpty(backupPath))
            {
                _consoleService.WriteMarkup($"[dim]:file_folder: Backup directory: {Markup.Escape(backupPath)}[/]\n");
            }
        }
        else if (!options.NoBackup)
        {
            var potentialBackupPath = Path.Combine(
                Path.GetFullPath(string.IsNullOrEmpty(options.BackupDir) ? "." : options.BackupDir),
                ".cpmigrate_backup");
            _consoleService.DryRun($"Would create backup directory: {potentialBackupPath}");
        }

        _consoleService.WriteLine();

        // Process each project with a nice progress bar
        var backupEntries = await ProcessProjectsWithProgressAsync(options, projectPaths, packages, backupPath);

        // Handle version conflicts
        var conflicts = _versionResolver.DetectConflicts(packages);
        if (conflicts.Count > 0)
        {
            _consoleService.WriteConflictsTable(packages, conflicts, options.ConflictStrategy);

            if (options.ConflictStrategy == ConflictStrategy.Fail)
            {
                _consoleService.Error("Version conflicts detected and --conflict-strategy is set to Fail.");
                _consoleService.WriteMarkup("[dim]Resolve the conflicts manually or use --conflict-strategy Highest|Lowest.[/]\n");
                return new MigrationResult { ExitCode = ExitCodes.VersionConflict };
            }
        }

        // Generate Directory.Packages.props
        var propsFilePath = await GeneratePropsFileAsync(options, packages);

        // Write backup manifest for rollback support
        if (!options.DryRun && !options.NoBackup && backupEntries.Count > 0 && !string.IsNullOrEmpty(backupPath))
        {
            var manifest = new BackupManifest
            {
                Timestamp = DateTime.Now.ToString("yyyyMMddHHmmss"),
                PropsFilePath = propsFilePath,
                Backups = backupEntries
            };
            await _backupManager.WriteManifestAsync(backupPath, manifest);
        }

        // Add to .gitignore if requested
        if (!options.DryRun)
        {
            await _backupManager.ManageGitIgnore(options, backupPath);
        }
        else if (options.AddBackupToGitignore && !options.NoBackup)
        {
            _consoleService.DryRun("Would add backup directory to .gitignore");
        }

        // Print summary
        _consoleService.WriteSummaryTable(
            projectPaths.Count,
            packages.Count,
            conflicts.Count,
            propsFilePath,
            backupPath,
            options.DryRun);

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
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

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
                    if (!options.DryRun && !options.NoBackup)
                    {
                        var backupFileName = $"{projectName}.backup_{timestamp}";
                        _backupManager.CreateBackupForProject(options, projectFilePath, backupPath!);

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
        var outputPath = Path.GetFullPath(options.OutputDir);
        var propsFilePath = Path.Combine(outputPath, "Directory.Packages.props");

        if (options.DryRun)
        {
            _consoleService.WriteLine();
            _consoleService.DryRun($"Would create: {propsFilePath}");
            _consoleService.WriteLine();
            _consoleService.WritePropsPreview(updatedPackagePropsContent);
        }
        else
        {
            await File.WriteAllTextAsync(propsFilePath, updatedPackagePropsContent);
            _consoleService.WriteMarkup($"\n[green]:page_facing_up: Generated:[/] [cyan]{Markup.Escape(propsFilePath)}[/]\n");
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
            _consoleService.Warning("No backup entries found in manifest.");
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

        // Delete Directory.Packages.props if it exists
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

        // Clean up backups
        _backupManager.CleanupBackups(backupPath, manifest);
        _consoleService.Dim("Cleaned up backup files.");

        // Summary
        _consoleService.WriteLine();
        if (failedCount == 0)
        {
            _consoleService.Success($"Rollback complete! Restored {restoredCount} file(s).");
        }
        else
        {
            _consoleService.Warning($"Rollback completed with errors. Restored: {restoredCount}, Failed: {failedCount}");
        }

        return new MigrationResult
        {
            ProjectsProcessed = restoredCount,
            ExitCode = failedCount == 0 ? ExitCodes.Success : ExitCodes.FileOperationError
        };
    }
}