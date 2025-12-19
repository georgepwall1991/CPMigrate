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
        var graphService = new DependencyGraphService(_consoleService);
        _analysisService = analysisService ?? new AnalysisService(null, graphService);
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
        var propsFileExists = IsAlreadyMigrated(propsPath);
        string? backupPath = null;
        string? backupTimestamp = null;
        bool backupsCreated = false;

        try
        {
            if (!options.DryRun)
            {
                var directoryError = await ValidateOutputDirectoryAsync(outputPath);
                if (directoryError != null)
                {
                    return directoryError;
                }
                
                // Warn about unstaged changes if in a git repo
                await CheckForUnstagedChangesAsync(outputPath);
            }

            if (propsFileExists && !options.MergeExisting)
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
            var propsFileExisted = propsFileExists;
            var hadConditionalPackageVersions = false;

            if (propsFileExists && options.MergeExisting)
            {
                if (!TryLoadExistingPropsPackages(propsPath, packages, out var existingCount, out hadConditionalPackageVersions))
                {
                    return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
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

            backupPath = SetupBackupDirectory(options);
            backupTimestamp = !options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath)
                ? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                : null;
            BackupEntry? propsBackupEntry = null;

            if (propsFileExists && options.MergeExisting && !options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath))
            {
                propsBackupEntry = _backupManager.CreateBackupForProject(options, propsPath, backupPath, backupTimestamp);
                if (propsBackupEntry != null && !_quietMode)
                {
                    _consoleService.Dim("Backed up existing Directory.Packages.props.");
                }
            }

            var backupEntries = await ProcessProjectsWithProgressAsync(options, projectPaths, packages, backupPath, backupTimestamp);
            backupsCreated = backupEntries.Count > 0 || propsBackupEntry != null;

            if (propsBackupEntry != null)
            {
                backupEntries.Add(propsBackupEntry);
            }

            var conflicts = _versionResolver.DetectConflicts(packages);
            var conflictError = HandleVersionConflicts(options, packages, conflicts);
            if (conflictError != null)
            {
                // If we fail here due to conflicts, and we created backups, we might want to rollback
                // but usually failing on conflicts happens BEFORE writing files.
                // However, ProcessProjectsWithProgressAsync ALREADY wrote the modified project files.
                // So we MUST rollback if we fail here.
                if (backupsCreated && !options.DryRun)
                {
                    _consoleService.Warning("Migration failed during conflict resolution. Project files have already been modified.");
                    if (_consoleService.AskConfirmation("Rollback changes now?"))
                    {
                        await ExecuteRollbackAsync(new Options { BackupDir = backupPath!, Rollback = true });
                    }
                }
                return conflictError;
            }

            var propsFilePath = await GeneratePropsFileAsync(options, packages);

            await WriteBackupManifestAsync(options, backupEntries, backupPath, propsFilePath, propsFileExisted, backupTimestamp);
            await ManageGitIgnoreAsync(options, backupPath);

            ShowMigrationSummary(options, projectPaths.Count, packages.Count, conflicts.Count, propsFilePath, backupPath);
            ShowPostMigrationGuidance(options, propsFilePath);

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
            _consoleService.Error($"\nAn error occurred during migration: {ex.Message}");
            
            if (backupsCreated && !options.DryRun && !string.IsNullOrEmpty(backupPath))
            {
                _consoleService.Warning("Project files may have been partially modified.");
                if (_consoleService.AskConfirmation("Would you like to attempt an automatic rollback to the last backup?"))
                {
                    await ExecuteRollbackAsync(new Options { BackupDir = backupPath, Rollback = true });
                }
            }
            
            throw; // Re-throw to be handled by Program.cs or caller
        }
    }

    /// <summary>
    /// Checks for unstaged changes in the target directory and warns the user.
    /// </summary>
    private async Task CheckForUnstagedChangesAsync(string directory)
    {
        if (_quietMode) return;

        try
        {
            // Simple check using git status --porcelain
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.Arguments = "status --porcelain";
            process.StartInfo.WorkingDirectory = directory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                _consoleService.Warning("Unstaged changes detected in the repository.");
                _consoleService.Dim("It is highly recommended to commit or stash your changes before proceeding.");
                
                if (!_consoleService.AskConfirmation("Proceed anyway?"))
                {
                    throw new OperationCanceledException("User cancelled migration due to unstaged changes.");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Git not installed or not a repo, ignore */ }
    }

    private void ShowPostMigrationGuidance(Options options, string propsFilePath)
    {
        if (_quietMode || options.DryRun) return;

        _consoleService.WriteLine();
        _consoleService.Banner("NEXT STEPS & VERIFICATION");
        _consoleService.WriteLine();
        _consoleService.Info("1. Review the generated file: [cyan]" + Markup.Escape(propsFilePath) + "[/]");
        _consoleService.Info("2. If you encounter issues, you can rollback using: [white]cpmigrate --rollback[/]");
        _consoleService.WriteLine();

        if (_consoleService.AskConfirmation("Would you like to verify the migration now by running 'dotnet restore'?"))
        {
            _consoleService.WriteLine();
            var success = RunDotnetRestore(Path.GetDirectoryName(propsFilePath) ?? ".");
            if (success)
            {
                _consoleService.Success("Verification successful! All projects restored correctly.");
            }
            else
            {
                _consoleService.Error("Verification failed. Some projects have restore errors.");
                _consoleService.Warning("You might need to resolve version conflicts manually or rollback.");
            }
        }

        _consoleService.WriteLine();
        _consoleService.Success("Migration completed successfully! 🎉");
    }

    private bool RunDotnetRestore(string workingDirectory)
    {
        return AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .Start("Running dotnet restore...", ctx =>
            {
                try
                {
                    using var process = new System.Diagnostics.Process();
                    process.StartInfo.FileName = "dotnet";
                    process.StartInfo.Arguments = "restore";
                    process.StartInfo.WorkingDirectory = workingDirectory;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                catch
                {
                    return false;
                }
            });
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
        _consoleService.Info("To merge into the existing props file, use: cpmigrate --merge");
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
            var existingPackages = _propsGenerator.ReadExistingPackageVersions(
                propsFilePath, out hasConditionalPackageVersions);
            existingPackageCount = existingPackages.Count;

            foreach (var kvp in existingPackages)
            {
                if (packages.TryGetValue(kvp.Key, out var versions))
                {
                    versions.UnionWith(kvp.Value);
                }
                else
                {
                    packages.Add(kvp.Key, new HashSet<string>(kvp.Value));
                }
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

            // We need to count usage across all projects to show impact
            var usageCounts = new Dictionary<string, Dictionary<string, int>>();
            var (basePath, projectPaths) = DiscoverProjects(options);
            foreach (var path in projectPaths)
            {
                var (refs, _) = _projectAnalyzer.ScanProjectPackages(path);
                foreach (var r in refs)
                {
                    if (!usageCounts.ContainsKey(r.PackageName)) usageCounts[r.PackageName] = new Dictionary<string, int>();
                    if (!usageCounts[r.PackageName].ContainsKey(r.Version)) usageCounts[r.PackageName][r.Version] = 0;
                    usageCounts[r.PackageName][r.Version]++;
                }
            }

            foreach (var packageName in conflicts)
            {
                if (!packages.TryGetValue(packageName, out var versions)) continue;

                var versionList = versions.OrderByDescending(v => v).ToList();
                var recommended = _versionResolver.ResolveVersion(versions, options.ConflictStrategy);
                
                var choices = versionList.Select(v => {
                    var count = usageCounts.ContainsKey(packageName) && usageCounts[packageName].ContainsKey(v) 
                        ? usageCounts[packageName][v] : 1;
                    var label = $"{v} (Used by {count} project{(count == 1 ? "" : "s")})";
                    if (v == recommended) label += " [springgreen1]**Recommended**[/]";
                    return label;
                }).ToList();

                var selected = _consoleService.AskSelection($"Version for {packageName}?", choices);
                var selectedVersion = selected.Split(' ')[0];

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
        Dictionary<string, HashSet<string>> packages, string? backupPath, string? backupTimestamp)
    {
        var backupEntries = new List<BackupEntry>();

        // Process without progress bar in quiet mode
        if (_quietMode)
        {
            foreach (var projectFilePath in projectPaths)
            {
                var projectName = Path.GetFileName(projectFilePath);

                // Backup
                if (!options.DryRun && !options.NoBackup && !string.IsNullOrEmpty(backupPath))
                {
                    var backupEntry = _backupManager.CreateBackupForProject(
                        options, projectFilePath, backupPath, backupTimestamp);
                    if (backupEntry != null)
                    {
                        backupEntries.Add(backupEntry);
                    }
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
                        var backupEntry = _backupManager.CreateBackupForProject(
                            options, projectFilePath, backupPath, backupTimestamp);
                        if (backupEntry != null)
                        {
                            backupEntries.Add(backupEntry);
                        }
                    }

                    // Process project file
                    var projectFileContent = _projectAnalyzer.ProcessProject(
                        projectFilePath, packages, options.KeepAttributes);

                    if (options.IncludeTransitive)
                    {
                        task.Description = $"[cyan]Scanning transitive[/] [white]{Markup.Escape(projectName)}[/]";
                        var (transitiveRefs, transitiveSuccess) = await _projectAnalyzer.ScanTransitivePackagesAsync(projectFilePath);
                        if (transitiveSuccess)
                        {
                            foreach (var tr in transitiveRefs)
                            {
                                if (packages.TryGetValue(tr.PackageName, out var versions))
                                    versions.Add(tr.Version);
                                else
                                    packages.Add(tr.PackageName, new HashSet<string> { tr.Version });
                            }
                        }
                    }

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
        var outputDir = string.IsNullOrEmpty(options.OutputDir) ? "." : options.OutputDir;
        var outputPath = Path.GetFullPath(outputDir);
        var propsFilePath = Path.Combine(outputPath, "Directory.Packages.props");
        var shouldMerge = options.MergeExisting && File.Exists(propsFilePath);

        if (shouldMerge)
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
                await File.WriteAllTextAsync(propsFilePath, mergedContent);
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
        var propsFileExisted = manifest.PropsFileExisted;
        var propsFilePath = manifest.PropsFilePath;

        // Only delete Directory.Packages.props if ALL files restored successfully
        if (failedCount == 0)
        {
            if (!string.IsNullOrEmpty(propsFilePath) && !propsFileExisted && File.Exists(propsFilePath))
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
            else if (propsFileExisted)
            {
                _consoleService.Dim("Preserved existing Directory.Packages.props.");
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
            _consoleService.Warning(propsFileExisted
                ? "Existing props file retained due to restore failures."
                : "Props file NOT deleted due to restore failures.");
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
            // Parse timestamp (supports legacy and millisecond formats)
            var displayTime = "Unknown";
            var timestampFormats = new[] { "yyyyMMddHHmmssfff", "yyyyMMddHHmmss", "yyyyMMddHHmmssZ" };
            if (DateTime.TryParseExact(backup.Timestamp, timestampFormats,
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
        var allVulnerabilities = new List<VulnerabilityInfo>();
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
                    
                    if (options.IncludeTransitive || options.AuditSecurity)
                    {
                        task.Description = $"[cyan]Deep scanning[/] [white]{Markup.Escape(projectName)}[/]";
                        
                        if (options.IncludeTransitive)
                        {
                            var (transitiveRefs, transitiveSuccess) = await _projectAnalyzer.ScanTransitivePackagesAsync(projectPath);
                            if (transitiveSuccess) allReferences.AddRange(transitiveRefs);
                        }

                        if (options.AuditSecurity)
                        {
                            var (vulnerabilities, auditSuccess) = await _projectAnalyzer.ScanVulnerabilitiesAsync(projectPath);
                            if (auditSuccess) allVulnerabilities.AddRange(vulnerabilities);
                        }
                    }

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

        var packageInfo = new ProjectPackageInfo(allReferences, allVulnerabilities);

        // Write header
        _consoleService.WriteAnalysisHeader(packageInfo.ProjectCount, packageInfo.TotalReferences, packageInfo.VulnerabilityCount);

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
