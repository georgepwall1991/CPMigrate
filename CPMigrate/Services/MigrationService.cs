using CPMigrate.Analyzers;
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

    public MigrationService(
        IConsoleService consoleService,
        ProjectAnalyzer? projectAnalyzer = null,
        VersionResolver? versionResolver = null,
        PropsGenerator? propsGenerator = null,
        BackupManager? backupManager = null,
        AnalysisService? analysisService = null)
    {
        _consoleService = consoleService;
        _versionResolver = versionResolver ?? new VersionResolver(_consoleService);
        _projectAnalyzer = projectAnalyzer ?? new ProjectAnalyzer(_consoleService);
        _propsGenerator = propsGenerator ?? new PropsGenerator(_versionResolver);
        _backupManager = backupManager ?? new BackupManager();
        _analysisService = analysisService ?? new AnalysisService();
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

        // Handle analyze mode
        if (options.Analyze)
        {
            return await ExecuteAnalysisAsync(options);
        }

        // Check if already migrated to CPM
        var outputDir = string.IsNullOrEmpty(options.OutputDir) ? "." : options.OutputDir;
        var outputPath = Path.GetFullPath(outputDir);
        var propsPath = Path.Combine(outputPath, "Directory.Packages.props");

        // Validate output directory exists and is writable before making any changes
        if (!options.DryRun)
        {
            if (!Directory.Exists(outputPath))
            {
                _consoleService.Error($"Output directory does not exist: {outputPath}");
                return new MigrationResult { ExitCode = ExitCodes.ValidationError };
            }

            // Test write access by attempting to create and delete a temp file
            var testFile = Path.Combine(outputPath, $".cpmigrate_test_{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllTextAsync(testFile, "test");
            }
            catch (Exception ex)
            {
                _consoleService.Error($"Cannot write to output directory: {outputPath}");
                _consoleService.Dim($"Error: {ex.Message}");
                return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
            }
            finally
            {
                // Always attempt cleanup even if an exception occurred after write
                try { if (File.Exists(testFile)) File.Delete(testFile); } catch { /* Ignore cleanup errors */ }
            }
        }

        if (File.Exists(propsPath))
        {
            _consoleService.Warning("This solution has already been migrated to Central Package Management.");
            _consoleService.WriteMarkup($"[dim]Found existing:[/] [cyan]{Markup.Escape(propsPath)}[/]\n");
            _consoleService.WriteLine();
            _consoleService.Info("To re-migrate, first rollback with: cpmigrate --rollback");
            _consoleService.Info("To analyze packages, use: cpmigrate --analyze");
            return new MigrationResult { ExitCode = ExitCodes.Success, PropsFilePath = propsPath };
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
                Timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssZ"),
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
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssZ");

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
            _backupManager.CleanupBackups(backupPath, manifest);
            _consoleService.Dim("Cleaned up backup files.");
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

        return new MigrationResult
        {
            ProjectsProcessed = packageInfo.ProjectCount,
            PackagesCentralized = packageInfo.TotalReferences,
            ExitCode = report.HasIssues ? ExitCodes.AnalysisIssuesFound : ExitCodes.Success
        };
    }
}