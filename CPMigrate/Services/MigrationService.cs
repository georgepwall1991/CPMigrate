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

    public MigrationService(
        ProjectAnalyzer? projectAnalyzer = null,
        VersionResolver? versionResolver = null,
        PropsGenerator? propsGenerator = null,
        BackupManager? backupManager = null)
    {
        _versionResolver = versionResolver ?? new VersionResolver();
        _projectAnalyzer = projectAnalyzer ?? new ProjectAnalyzer();
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
        ConsoleOutput.WriteHeader();

        // Dry-run banner
        if (options.DryRun)
        {
            ConsoleOutput.Banner("DRY-RUN MODE - No files will be modified");
            AnsiConsole.WriteLine();
        }

        // Validate options
        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            ConsoleOutput.Error(ex.Message);
            return new MigrationResult { ExitCode = ExitCodes.ValidationError };
        }

        // Discover projects with a spinner
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
            ConsoleOutput.Error("No projects found to process.");
            return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
        }

        // Show discovered projects
        AnsiConsole.MarkupLine($"\n[green]:magnifying_glass_tilted_right: Found {projectPaths.Count} project(s)[/]\n");

        if (!string.IsNullOrEmpty(basePath))
        {
            ConsoleOutput.WriteProjectTree(projectPaths, basePath);
        }

        var packages = new Dictionary<string, HashSet<string>>();

        // Create backup directory
        string? backupPath = null;
        if (!options.DryRun)
        {
            backupPath = _backupManager.CreateBackupDirectory(options);
            if (!string.IsNullOrEmpty(backupPath))
            {
                AnsiConsole.MarkupLine($"[dim]:file_folder: Backup directory: {Markup.Escape(backupPath)}[/]");
            }
        }
        else if (!options.NoBackup)
        {
            var potentialBackupPath = Path.Combine(
                Path.GetFullPath(string.IsNullOrEmpty(options.BackupDir) ? "." : options.BackupDir),
                ".cpmigrate_backup");
            ConsoleOutput.DryRun($"Would create backup directory: {potentialBackupPath}");
        }

        AnsiConsole.WriteLine();

        // Process each project with a nice progress bar
        await ProcessProjectsWithProgressAsync(options, projectPaths, packages, backupPath);

        // Handle version conflicts
        var conflicts = _versionResolver.DetectConflicts(packages);
        if (conflicts.Count > 0)
        {
            ConsoleOutput.WriteConflictsTable(packages, conflicts, options.ConflictStrategy);

            if (options.ConflictStrategy == ConflictStrategy.Fail)
            {
                ConsoleOutput.Error("Version conflicts detected and --conflict-strategy is set to Fail.");
                AnsiConsole.MarkupLine("[dim]Resolve the conflicts manually or use --conflict-strategy Highest|Lowest.[/]");
                return new MigrationResult { ExitCode = ExitCodes.VersionConflict };
            }
        }

        // Generate Directory.Packages.props
        var propsFilePath = await GeneratePropsFileAsync(options, packages);

        // Add to .gitignore if requested
        if (!options.DryRun)
        {
            await _backupManager.ManageGitIgnore(options, backupPath);
        }
        else if (options.AddBackupToGitignore && !options.NoBackup)
        {
            ConsoleOutput.DryRun("Would add backup directory to .gitignore");
        }

        // Print summary
        ConsoleOutput.WriteSummaryTable(
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

        ConsoleOutput.Error("Either solution (-s) or project (-p) path must be specified.");
        return (string.Empty, new List<string>());
    }

    private async Task ProcessProjectsWithProgressAsync(Options options, List<string> projectPaths,
        Dictionary<string, HashSet<string>> packages, string? backupPath)
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
                var task = ctx.AddTask("[cyan]Processing projects[/]", maxValue: projectPaths.Count);

                foreach (var projectFilePath in projectPaths)
                {
                    var projectName = Path.GetFileName(projectFilePath);
                    task.Description = $"[cyan]Processing[/] [white]{Markup.Escape(projectName)}[/]";

                    // Backup
                    if (!options.DryRun && !options.NoBackup)
                    {
                        _backupManager.CreateBackupForProject(options, projectFilePath, backupPath!);
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

        AnsiConsole.WriteLine();
    }

    private async Task<string> GeneratePropsFileAsync(Options options,
        Dictionary<string, HashSet<string>> packages)
    {
        var updatedPackagePropsContent = _propsGenerator.Generate(packages, options.ConflictStrategy);
        var outputPath = Path.GetFullPath(options.OutputDir);
        var propsFilePath = Path.Combine(outputPath, "Directory.Packages.props");

        if (options.DryRun)
        {
            AnsiConsole.WriteLine();
            ConsoleOutput.DryRun($"Would create: {propsFilePath}");
            AnsiConsole.WriteLine();
            ConsoleOutput.WritePropsPreview(updatedPackagePropsContent);
        }
        else
        {
            await File.WriteAllTextAsync(propsFilePath, updatedPackagePropsContent);
            AnsiConsole.MarkupLine($"\n[green]:page_facing_up: Generated:[/] [cyan]{Markup.Escape(propsFilePath)}[/]");
        }

        return propsFilePath;
    }
}
