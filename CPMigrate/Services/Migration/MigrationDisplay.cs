using CPMigrate.Models;

namespace CPMigrate.Services.Migration;

/// <summary>
/// Handles display and user guidance during migration operations.
/// </summary>
internal class MigrationDisplay
{
    private readonly IConsoleService _consoleService;

    public MigrationDisplay(IConsoleService consoleService)
    {
        _consoleService = consoleService;
    }

    /// <summary>
    /// Shows dry-run banner if in dry-run mode.
    /// </summary>
    public void ShowDryRunBannerIfNeeded(Options options)
    {
        if (options.DryRun)
        {
            _consoleService.DryRun("DRY RUN MODE - No files will be modified");
            _consoleService.WriteLine();
        }
    }

    /// <summary>
    /// Shows the list of discovered projects.
    /// </summary>
    public void ShowDiscoveredProjects(string basePath, List<string> projectPaths)
    {
        _consoleService.Info($"Found {projectPaths.Count} project(s) in {basePath}:");
        foreach (var projectPath in projectPaths)
        {
            var displayPath = projectPath.Replace(basePath, "").TrimStart(Path.DirectorySeparatorChar);
            _consoleService.Dim($"  • {displayPath}");
        }
        _consoleService.WriteLine();
    }

    /// <summary>
    /// Shows migration summary after completion.
    /// </summary>
    public void ShowMigrationSummary(
        int projectsProcessed,
        int packagesFound,
        int conflictsResolved,
        string propsPath,
        bool wasDryRun)
    {
        _consoleService.WriteLine();
        _consoleService.Success("Migration completed successfully!");
        _consoleService.Info($"  Projects processed: {projectsProcessed}");
        _consoleService.Info($"  Packages centralized: {packagesFound}");

        if (conflictsResolved > 0)
        {
            _consoleService.Info($"  Conflicts resolved: {conflictsResolved}");
        }

        if (!wasDryRun)
        {
            _consoleService.Info($"  Directory.Packages.props: {propsPath}");
        }
    }

    /// <summary>
    /// Shows post-migration guidance to the user.
    /// </summary>
    public void ShowPostMigrationGuidance(Options options, string propsFilePath)
    {
        _consoleService.WriteLine();
        _consoleService.Banner("NEXT STEPS");
        _consoleService.Info("1. Review the generated Directory.Packages.props file");
        _consoleService.Dim($"   Location: {propsFilePath}");

        _consoleService.Info("2. Restore packages and build:");
        _consoleService.Dim("   dotnet restore && dotnet build");

        _consoleService.Info("3. Commit the changes:");
        _consoleService.Dim("   git add .");
        _consoleService.Dim("   git commit -m \"Migrate to Central Package Management\"");
        _consoleService.WriteLine();

        if (!options.NoBackup)
        {
            _consoleService.Dim("💡 Tip: A backup was created. Use --rollback to undo if needed.");
            _consoleService.WriteLine();
        }
    }

    /// <summary>
    /// Creates and shows a result for when directory is already migrated.
    /// </summary>
    public MigrationResult CreateAlreadyMigratedResult(string propsPath)
    {
        _consoleService.Info($"Directory.Packages.props already exists at {propsPath}");
        _consoleService.Info("This directory appears to be already migrated to CPM.");
        _consoleService.Dim("Use --force to overwrite the existing file, or delete it manually.");

        return new MigrationResult
        {
            ExitCode = ExitCodes.Success,
            PropsFilePath = propsPath
        };
    }
}
