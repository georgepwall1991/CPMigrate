using CPMigrate.Models;
using Spectre.Console;

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
        _consoleService.Banner("NEXT STEPS & VERIFICATION");
        _consoleService.WriteLine();
        _consoleService.Info($"1. Review the generated file: {propsFilePath}");
        _consoleService.Info("2. If you encounter issues, you can rollback using: cpmigrate --rollback");
        _consoleService.WriteLine();

        if (ShouldOfferVerification(options) &&
            _consoleService.AskConfirmation("Would you like to verify the migration now by running 'dotnet restore'?"))
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

        if (!options.NoBackup)
        {
            _consoleService.Dim("💡 Tip: A backup was created. Use --rollback to undo if needed.");
            _consoleService.WriteLine();
        }
    }

    private static bool ShouldOfferVerification(Options options)
    {
        // Skip the interactive "Would you like to verify the migration now?" prompt under any
        // non-interactive condition: --force (operator opted out of prompts), --quiet, or JSON
        // output. Without this guard, the prompt throws "Cannot show selection prompt" when the
        // CLI runs in a non-TTY shell (e.g. CI, scripts).
        if (options.Force)
        {
            return false;
        }

        if (options.Output == OutputFormat.Json)
        {
            return false;
        }

        if (options.Quiet)
        {
            return false;
        }

        return true;
    }

    private static bool RunDotnetRestore(string workingDirectory)
    {
        return AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .Start("Running dotnet restore...", ctx =>
            {
                try
                {
                    using var process = new System.Diagnostics.Process();
                    // Security: Try to use the absolute path of the dotnet host to avoid PATH injection
                    var dotnetPath = "dotnet";
                    try
                    {
                        var mainModule = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrEmpty(mainModule) &&
                            (mainModule.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                             mainModule.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)))
                        {
                            dotnetPath = mainModule;
                        }
                    }
                    catch
                    {
                        // Fallback to simpler PATH resolution if module access fails
                    }
                    process.StartInfo.FileName = dotnetPath;
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
