using CPMigrate.Models;
using CPMigrate.Services.Verify;
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
            var displayPath = projectPath
                .Replace(basePath, "")
                .TrimStart(Path.DirectorySeparatorChar);
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
        bool wasDryRun
    )
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
        _consoleService.Info(
            "2. If you encounter issues, you can rollback using: cpmigrate --rollback"
        );
        _consoleService.WriteLine();

        if (ShouldOfferVerification(options, _consoleService))
        {
            _consoleService.Dim(
                "   --verify does this without asking, and compares the resolved graph rather than "
                    + "just the restore exit code."
            );
            _consoleService.WriteLine();

            if (
                _consoleService.AskConfirmation(
                    "Would you like to verify the migration now by running 'dotnet restore'?"
                )
            )
            {
                _consoleService.WriteLine();
                var success = RunDotnetRestore(Path.GetDirectoryName(propsFilePath) ?? ".");
                if (success)
                {
                    _consoleService.Success(
                        "Verification successful! All projects restored correctly."
                    );
                }
                else
                {
                    _consoleService.Error(
                        "Verification failed. Some projects have restore errors."
                    );
                    _consoleService.Warning(
                        "You might need to resolve version conflicts manually or rollback."
                    );
                }
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

    private static bool ShouldOfferVerification(Options options, IConsoleService console)
    {
        // --verify already restored twice and compared the results. Offering to run a third restore
        // whose only measure is an exit code would be both slower and weaker than what just ran.
        if (options.Verify)
        {
            return false;
        }

        // Skip the interactive "Would you like to verify the migration now?" prompt under any
        // non-interactive condition: --force (operator opted out of prompts), --quiet, or JSON
        // output. Without this guard, the prompt throws "Cannot show selection prompt" when the
        // CLI runs in a non-TTY shell (e.g. CI, scripts).
        if (options.Force)
        {
            return false;
        }

        if (options.Output.IsMachineReadable())
        {
            return false;
        }

        if (options.Quiet)
        {
            return false;
        }

        // A dry run changed nothing on disk, so there is nothing for `dotnet restore` to verify.
        if (options.DryRun)
        {
            return false;
        }

        // The flag checks above only cover the non-TTY cases the operator opted into. A plain
        // `cpmigrate migrate` with stdout redirected (CI logs, `| tee`) has none of them set and
        // still cannot service a prompt, so ask the console itself.
        return console.IsInteractive;
    }

    private static bool RunDotnetRestore(string workingDirectory)
    {
        return AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .Start(
                "Running dotnet restore...",
                ctx =>
                {
                    try
                    {
                        using var process = new System.Diagnostics.Process();
                        // Security: Try to use the absolute path of the dotnet host to avoid PATH injection
                        var dotnetPath = "dotnet";
                        try
                        {
                            var mainModule = System
                                .Diagnostics.Process.GetCurrentProcess()
                                .MainModule?.FileName;
                            if (
                                !string.IsNullOrEmpty(mainModule)
                                && (
                                    mainModule.EndsWith(
                                        "dotnet",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    || mainModule.EndsWith(
                                        "dotnet.exe",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                            )
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
                }
            );
    }

    /// <summary>
    /// Renders the resolved-graph verification receipt.
    /// </summary>
    /// <remarks>
    /// Written so the counts come first and the verdict last. A reviewer reading a CI log wants to
    /// know how much was measured before being told what it means — "0 changed" over four projects
    /// out of forty is a different statement from "0 changed" over all of them, and the table is what
    /// makes the difference visible.
    /// </remarks>
    public void ShowVerificationReport(VerificationReport report, bool strict, bool quiet)
    {
        if (quiet)
        {
            return;
        }

        _consoleService.WriteLine();
        _consoleService.Banner("RESOLVED-GRAPH VERIFICATION");
        _consoleService.WriteLine();

        if (report.Verdict == VerificationVerdict.Failed)
        {
            ShowVerificationFailure(report);
            return;
        }

        _consoleService.Info(
            $"  Projects restored     {report.ProjectsRestored} / {report.ProjectsExpected}"
        );
        _consoleService.Info($"  Resolved versions     {report.ResolvedVersionCount}");
        _consoleService.Info($"  Unchanged             {report.UnchangedCount}");
        _consoleService.Info($"  Changed               {report.ChangedCount}");

        foreach (
            var group in report.Changes.GroupBy(change =>
                (change.Change.ProjectPath, change.Change.TargetFramework)
            )
        )
        {
            _consoleService.WriteLine();
            _consoleService.Info(
                // The relative path, not the file name: src/Api/Api.csproj and tests/Api/Api.csproj
                // are two projects, and a receipt calling both "Api.csproj" cannot be acted on.
                $"  {group.Key.ProjectPath} [{group.Key.TargetFramework}]"
            );

            foreach (var change in group)
            {
                ShowChange(change);
            }
        }

        _consoleService.WriteLine();
        ShowVerdict(report, strict);
    }

    private void ShowChange(AttributedChange change)
    {
        var movement = change.Change.Kind switch
        {
            GraphChangeKind.Added => $"added at {change.Change.After}",
            GraphChangeKind.Removed => $"removed (was {change.Change.Before})",
            _ => $"{change.Change.Before} → {change.Change.After}",
        };

        var line = $"    {change.Change.PackageId}  {movement}  — {change.Description}";

        if (change.Kind == DriftExplanation.Unexplained)
        {
            _consoleService.Warning($"    UNEXPLAINED  {change.Change.PackageId}  {movement}");
            return;
        }

        _consoleService.Dim(line);
    }

    private void ShowVerificationFailure(VerificationReport report)
    {
        _consoleService.Error($"Verification could not reach a verdict: {report.FailureReason}");

        foreach (var failure in report.IntegrityFailures)
        {
            var where = failure.TargetFramework is null
                ? failure.ProjectPath
                : $"{failure.ProjectPath} [{failure.TargetFramework}]";

            _consoleService.Warning($"  {where}: {failure.Reason}");
        }

        _consoleService.WriteLine();
        _consoleService.Warning(
            "Not knowing whether the graph moved is not the same as knowing it did not."
        );
    }

    private void ShowVerdict(VerificationReport report, bool strict)
    {
        switch (report.Verdict)
        {
            case VerificationVerdict.Unchanged:
                _consoleService.Success(
                    "VERDICT  every resolved version is exactly what it was. This migration changes no shipped code."
                );
                break;

            case VerificationVerdict.ExplainedDrift when strict:
                _consoleService.Error(
                    $"VERDICT  {report.ChangedCount} resolved version(s) moved. Every one is accounted for, "
                        + "but --verify-strict asked for a migration that changes nothing at all."
                );
                _consoleService.Dim(
                    "   The migration is left in place so it can be read. Undo it with cpmigrate --rollback."
                );
                break;

            case VerificationVerdict.ExplainedDrift:
                _consoleService.Success(
                    $"VERDICT  {report.ChangedCount} resolved version(s) moved, all accounted for by "
                        + $"{report.Decisions.Count} deliberate decision(s)."
                );
                break;

            default:
                _consoleService.Error(
                    $"VERDICT  {report.UnexplainedCount} of {report.ChangedCount} changed version(s) are not "
                        + "accounted for by anything this migration decided."
                );
                break;
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

        return new MigrationResult { ExitCode = ExitCodes.Success, PropsFilePath = propsPath };
    }
}
