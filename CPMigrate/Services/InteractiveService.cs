using CPMigrate.Models;
using CPMigrate.Services.Interactive;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Implementation of interactive wizard mode for CPMigrate.
/// Guides users through options using Spectre.Console prompts.
/// </summary>
public class InteractiveService : IInteractiveService
{
    private readonly IConsoleService _console;
    private readonly EnvironmentAnalyzer _environmentAnalyzer;

    private const string ModeMigrate = "🚀 Migrate to Central Package Management";
    private const string ModeAnalyze = "🔍 Analyze packages for issues";
    private const string ModeBatch = "📦 Batch migrate multiple solutions";
    private const string ModeRollback = "↩️  Rollback a previous migration";
    private const string ModeBackups = "💾 Manage backups (List/Prune)";
    private const string ModeUpdatePackages = "📡 Update NuGet packages to latest versions";
    private const string ModeUnifyProps = "🏗  Unify Directory.Build.props (Clean Architecture)";

    private const string ConflictHighest = "⬆️  Highest version (recommended)";
    private const string ConflictLowest = "⬇️  Lowest version";
    private const string ConflictFail = "⛔️ Fail on conflict";
    private const string ConflictInteractive = "🤝 Resolve each conflict interactively";

    private const string EnterPathManually = "✏️  Enter path manually...";

    private readonly string? _workingDirectory;

    public InteractiveService(IConsoleService console, string? workingDirectory = null)
    {
        _console = console;
        _workingDirectory = workingDirectory;
        _environmentAnalyzer = new EnvironmentAnalyzer(console, workingDirectory);
    }

    /// <inheritdoc />
    public Options? RunWizard()
    {
        try
        {
            _console.WriteHeader();
            var context = _environmentAnalyzer.Analyze();
            _console.WriteStatusDashboard(context.Directory, context.Solutions, context.Backups, context.IsGitRepo, context.HasUnstaged, context.TargetFrameworks);

            if (context.ConflictCount > 0 || context.ProjectCount > 0)
            {
                _console.WriteRiskScore(context.ConflictCount, context.ProjectCount);
            }

            var options = new Options();

            // Step 1: Intelligent Quick Actions
            var action = AskQuickAction(context);
            if (action == "Exit")
            {
                return null;
            }

            var result = ConfigureActionOptions(action, context, options);
            if (result.EarlyReturn)
            {
                return options; // Batch or backup management configured their own options
            }

            if (result.NeedsPath)
            {
                var path = AskSolutionPath();
                if (path == null)
                {
                    return null;
                }

                options.SolutionFileDir = path;
                options.OutputDir = path;
            }

            // Refine options if not fast-tracked
            if (options.Analyze)
            {
                AskAnalyzeOptions(options);
            }
            else if (options.Rollback)
            {
                AskRollbackOptions(options);
            }
            else if (options.UpdatePackages)
            {
                AskUpdatePackagesOptions(options);
            }
            else if (options.UnifyProps)
            { /* No extra options for unify currently */ }
            else if (string.IsNullOrEmpty(options.BatchDir) && !options.InteractiveConflicts)
            {
                AskMigrationOptions(options);
            }

            ShowSummary(options, action);

            if (!AskConfirmation())
            {
                _console.Info("Operation cancelled.");
                return null;
            }

            return options;
        }
        catch (OperationCanceledException)
        {
            _console.WriteLine();
            _console.Info("Cancelled.");
            return null;
        }
    }

    private string AskQuickAction(EnvironmentContext ctx)
    {
        var migrationActions = new List<string>();
        var maintenanceActions = new List<string>();
        var systemActions = new List<string>();

        // 1. Migration Actions
        if (!ctx.IsCpm && ctx.ProjectCount > 0)
        {
            var label = ctx.ConflictCount > 0
                ? $"🚀 Fast-Track Migration (Auto-resolve {ctx.ConflictCount} conflicts)"
                : "⚡️ Migrate to Central Package Management (Clean Path)";
            migrationActions.Add(label);

            if (ctx.ConflictCount > 0)
            {
                migrationActions.Add("🛠  Migrate & Review Conflicts Individually");
            }
        }
        else if (ctx.IsCpm)
        {
            migrationActions.Add("🔍 Analyze current CPM setup for issues");
            migrationActions.Add("🛡  Security Audit (Scan for vulnerabilities)");
        }

        migrationActions.Add("⚙️  Custom Migration (Manual Setup)");

        // 2. Maintenance Actions
        if (ctx.IsCpm)
        {
            maintenanceActions.Add(ModeUpdatePackages);
        }

        maintenanceActions.Add(ModeUnifyProps);
        maintenanceActions.Add("📦 Batch migrate multiple solutions");

        if (ctx.Backups.Count > 0)
        {
            maintenanceActions.Add("↩️  Rollback to a previous state");
        }

        maintenanceActions.Add("💾 Manage Backups");

        // 3. System
        systemActions.Add("Exit");

        // Build groups dictionary (all collections always have at least one item)
        var groups = new Dictionary<string, IEnumerable<string>>
        {
            ["MIGRATION ACTIONS"] = migrationActions,
            ["REPOSITORY MAINTENANCE"] = maintenanceActions,
            ["SYSTEM"] = systemActions
        };

        return _console.AskGroupedSelection("What's the mission?", groups);
    }

    private string? AskSolutionPath()
    {
        var currentDir = _workingDirectory ?? Directory.GetCurrentDirectory();
        return BrowseForPath(currentDir, "Select a solution, project, or directory to migrate");
    }

    private string? BrowseForPath(string rootPath, string title)
    {
        while (true)
        {
            var solutions = ProjectAnalyzer.GetSolutionFiles(rootPath)
                .Select(Path.GetFileName).Cast<string>().ToList();

            var projects = Directory.GetFiles(rootPath, "*.*proj", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".props") && !f.EndsWith(".targets"))
                .Select(Path.GetFileName).Cast<string>().ToList();

            var directories = Directory.GetDirectories(rootPath)
                .Select(d => Path.GetFileName(d) + "/")
                .Where(d => !BatchService.DefaultExcludedDirectories.Contains(d.TrimEnd('/')))
                .OrderBy(d => d)
                .ToList();

            var choices = new List<string>();

            // Add current directory as a choice if it contains projects or solutions
            if (solutions.Count > 0 || projects.Count > 0)
            {
                choices.Add($"🎯 Use current directory: {Path.GetFileName(rootPath) ?? rootPath}");
            }

            // Add solutions and projects
            choices.AddRange(solutions.Select(s => $"🟦 Solution: {s}"));
            choices.AddRange(projects.Select(p => $"📗 Project: {p}"));

            // Add "Go Up" if not at root
            var parent = Directory.GetParent(rootPath);
            if (parent != null)
            {
                choices.Add("⬅️  Go up to parent directory");
            }

            // Add subdirectories
            choices.AddRange(directories.Select(d => $"📁 {d}"));
            choices.Add(EnterPathManually);

            var selection = _console.AskSelection(title, choices);

            if (selection == EnterPathManually)
            {
                var path = _console.AskText("Enter path manually (or leave empty to cancel)", ".");
                if (string.IsNullOrWhiteSpace(path) || path == ".")
                {
                    return null;
                }

                return Path.GetFullPath(path, _workingDirectory ?? Directory.GetCurrentDirectory());
            }

            if (selection.StartsWith("🎯 Use current"))
            {
                return rootPath;
            }

            if (selection == "⬅️  Go up to parent directory")
            {
                rootPath = parent!.FullName;
                continue;
            }

            if (selection.StartsWith("🟦 Solution:") || selection.StartsWith("📗 Project:"))
            {
                // For a specific file, we usually want the directory it's in
                return rootPath;
            }

            if (selection.StartsWith("📁 "))
            {
                var dirName = selection[3..].TrimEnd('/');
                rootPath = Path.Combine(rootPath, dirName);
                continue;
            }

            return null;
        }
    }

    private void AskAnalyzeOptions(Options options)
    {
        var transitiveChoice = _console.AskSelection(
            "Include transitive dependencies in analysis?",
            new[] { "No - direct references only (faster)", "Yes - full dependency tree (requires dotnet restore)" });

        options.IncludeTransitive = transitiveChoice.StartsWith("Yes");

        var fixChoice = _console.AskSelection(
            "Would you like to automatically fix issues?",
            new[] { "No - just report", "Yes - apply fixes", "Dry run - show proposed fixes" });

        options.Fix = fixChoice == "Yes - apply fixes";
        options.FixDryRun = fixChoice == "Dry run - show proposed fixes";
    }

    private void AskBatchOptions(Options options)
    {
        _console.Info("Scanning for a directory to batch process...");
        options.BatchDir = BrowseForPath(_workingDirectory ?? Directory.GetCurrentDirectory(), "Select the root directory for batch processing");

        var parallel = _console.AskSelection(
            "Process solutions in parallel?",
            new[] { "No - sequential (safer)", "Yes - parallel (faster)" });
        options.BatchParallel = parallel.StartsWith("Yes");

        var continueOnError = _console.AskSelection(
            "Continue if a solution fails?",
            new[] { "Yes", "No - stop on first error" });
        options.BatchContinue = continueOnError == "Yes";

        // Migration options for batch
        AskMigrationOptions(options);
    }

    private void AskBackupManagementOptions(Options options)
    {
        var action = _console.AskSelection(
            "Backup Management",
            new[] { "📊 List all backups", "🧹 Prune old backups", "🗑️  Delete ALL backups", "↩️  Back to main menu" });

        switch (action)
        {
            case "📊 List all backups":
                options.ListBackups = true;
                break;
            case "🧹 Prune old backups":
                options.PruneBackups = true;
                options.Retention = _console.AskInt("How many recent backups should be kept?", 5);
                break;
            case "🗑️  Delete ALL backups":
                options.PruneAll = true;
                break;
        }
    }

    private void AskMigrationOptions(Options options)
    {
        // Conflict strategy
        var conflictChoice = _console.AskSelection(
            "Conflict resolution strategy?",
            new[] { ConflictHighest, ConflictLowest, ConflictInteractive, ConflictFail });

        if (conflictChoice == ConflictInteractive)
        {
            options.InteractiveConflicts = true;
            options.ConflictStrategy = ConflictStrategy.Highest; // Default if interactive fails
        }
        else
        {
            options.ConflictStrategy = conflictChoice switch
            {
                ConflictLowest => ConflictStrategy.Lowest,
                ConflictFail => ConflictStrategy.Fail,
                _ => ConflictStrategy.Highest
            };
        }

        // Backup option
        var createBackup = _console.AskSelection(
            "Create backup before migration?",
            new[] { "Yes (recommended)", "No" });

        options.NoBackup = createBackup == "No";

        if (!options.NoBackup)
        {
            var backupLoc = _console.AskSelection(
                "Where should the backup directory be created?",
                new[] { "Current directory (./.cpmigrate_backup)", "Choose a different directory" });

            if (backupLoc == "Current directory (./.cpmigrate_backup)")
            {
                options.BackupDir = ".";
            }
            else
            {
                options.BackupDir = BrowseForPath(_workingDirectory ?? Directory.GetCurrentDirectory(), "Select backup parent directory") ?? ".";
            }

            var addGitignore = _console.AskSelection(
                "Add backup directory to .gitignore?",
                new[] { "Yes", "No" });

            options.AddBackupToGitignore = addGitignore == "Yes";
            if (options.AddBackupToGitignore)
            {
                options.GitignoreDir = ".";
            }
        }

        // Dry run option
        var dryRun = _console.AskSelection(
            "Run as dry-run first?",
            new[] { "Yes - preview changes without modifying files", "No - make changes immediately" });

        options.DryRun = dryRun.StartsWith("Yes");

        // Keep attributes option
        var keepAttrs = _console.AskSelection(
            "Keep version attributes in project files?",
            new[] { "No - remove them (recommended for clean CPM)", "Yes - keep alongside CPM" });

        options.KeepAttributes = keepAttrs.StartsWith("Yes");

        // Transitive pinning
        var transitive = _console.AskSelection(
            "Pin transitive dependencies centrally?",
            new[] { "No (recommended for clean CPM)", "Yes - pin all transitive packages (prevents version drift)" });
        options.IncludeTransitive = transitive.StartsWith("Yes");

        // Merge existing props file if detected
        var propsFilePath = Path.Combine(Path.GetFullPath(options.SolutionFileDir ?? "."), "Directory.Packages.props");
        if (File.Exists(propsFilePath))
        {
            var mergeChoice = _console.AskSelection(
                "Directory.Packages.props already exists. How should CPMigrate proceed?",
                new[] { "Fail (recommended)", "Merge into existing file" });

            options.MergeExisting = mergeChoice.StartsWith("Merge");
        }
    }

    private void AskUpdatePackagesOptions(Options options)
    {
        var transitive = _console.AskSelection(
            "Include transitive dependencies?",
            new[] { "No - direct packages only", "Yes - include transitive dependencies" });
        options.IncludeTransitive = transitive.StartsWith("Yes");

        var prerelease = _console.AskSelection(
            "Include pre-release versions?",
            new[] { "No - stable versions only", "Yes - include pre-release versions" });
        options.IncludePrerelease = prerelease.StartsWith("Yes");

        var dryRun = _console.AskSelection(
            "Run as dry-run first?",
            new[] { "Yes - preview changes without modifying files", "No - make changes immediately" });
        options.DryRun = dryRun.StartsWith("Yes");
    }

    private void AskRollbackOptions(Options options)
    {
        _console.Info("Locating backup directory for rollback...");
        options.BackupDir = BrowseForPath(_workingDirectory ?? Directory.GetCurrentDirectory(), "Select the directory containing .cpmigrate_backup") ?? ".";
    }

    private void ShowSummary(Options options, string mode)
    {
        _console.WriteLine();

        var modeLabel = GetModeLabel(mode, options);
        var grid = CreateSummaryGrid(options, mode);

        var panel = new Panel(grid)
        {
            Header = new PanelHeader($"[deeppink1]READY TO {modeLabel}[/]", Justify.Center),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepPink1),
            Padding = new Padding(1, 1)
        };

        AnsiConsole.Write(panel);
        _console.WriteLine();
    }

    private static string GetModeLabel(string mode, Options options)
    {
        return mode switch
        {
            ModeMigrate => "MIGRATE",
            ModeAnalyze => "ANALYZE",
            ModeBatch => "BATCH MIGRATE",
            ModeRollback => "ROLLBACK",
            ModeBackups when options.PruneAll => "PRUNE ALL",
            ModeBackups when options.PruneBackups => "PRUNE",
            _ when options.UpdatePackages => "UPDATE PACKAGES",
            _ when mode.Contains("Unify") || options.UnifyProps => "UNIFY PROPS",
            _ => "UNKNOWN"
        };
    }

    private static Grid CreateSummaryGrid(Options options, string mode)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        AddPathRow(grid, options);
        AddModeSpecificRows(grid, options, mode);

        return grid;
    }

    private static void AddPathRow(Grid grid, Options options)
    {
        if (!string.IsNullOrEmpty(options.BatchDir))
        {
            grid.AddRow("[white]Batch Directory[/]", $"[cyan1]{EscapeMarkup(options.BatchDir)}[/]");
        }
        else if (!string.IsNullOrEmpty(options.SolutionFileDir))
        {
            grid.AddRow("[white]Solution/Project[/]", $"[cyan1]{EscapeMarkup(options.SolutionFileDir)}[/]");
        }
    }

    private static void AddModeSpecificRows(Grid grid, Options options, string mode)
    {
        if (mode == ModeMigrate || mode == ModeBatch)
        {
            AddMigrationRows(grid, options);
        }
        else if (mode == ModeAnalyze || mode.Contains("Analyze"))
        {
            AddAnalyzeRows(grid, options);
        }
        else if (mode == ModeRollback)
        {
            AddRollbackRows(grid, options);
        }
        else if (mode == ModeBackups)
        {
            AddBackupRows(grid, options);
        }
        else if (options.UpdatePackages)
        {
            AddUpdatePackagesRows(grid, options);
        }
        else if (options.UnifyProps)
        {
            AddUnifyPropsRows(grid);
        }
    }

    private static void AddMigrationRows(Grid grid, Options options)
    {
        grid.AddRow("[white]Conflict Strategy[/]", $"[cyan1]{(options.InteractiveConflicts ? "Interactive" : options.ConflictStrategy)}[/]");
        grid.AddRow("[white]Backup[/]", $"[cyan1]{(options.NoBackup ? "No" : $"Yes ({options.BackupDir})")}[/]");
        grid.AddRow("[white]Dry Run[/]", $"[cyan1]{(options.DryRun ? "Yes" : "No")}[/]");
        grid.AddRow("[white]Keep Version Attrs[/]", $"[cyan1]{(options.KeepAttributes ? "Yes" : "No")}[/]");
        grid.AddRow("[white]Pin Transitive[/]", $"[cyan1]{(options.IncludeTransitive ? "Yes" : "No")}[/]");

        if (options.MergeExisting)
        {
            grid.AddRow("[white]Merge Existing Props[/]", "[cyan1]Yes[/]");
        }
    }

    private static void AddAnalyzeRows(Grid grid, Options options)
    {
        grid.AddRow("[white]Transitive Deps[/]", $"[cyan1]{(options.IncludeTransitive ? "Yes" : "No")}[/]");
        var autoFixStatus = GetAutoFixStatus(options);
        grid.AddRow("[white]Auto-Fix[/]", $"[cyan1]{autoFixStatus}[/]");
    }

    private static string GetAutoFixStatus(Options options)
    {
        if (options.Fix)
        {
            return "Yes";
        }

        if (options.FixDryRun)
        {
            return "Dry Run";
        }

        return "No";
    }

    private static void AddRollbackRows(Grid grid, Options options)
    {
        grid.AddRow("[white]Backup Location[/]", $"[cyan1]{options.BackupDir}[/]");
    }

    private static void AddBackupRows(Grid grid, Options options)
    {
        if (options.PruneBackups)
        {
            grid.AddRow("[white]Retention[/]", $"[cyan1]Keep last {options.Retention}[/]");
        }
        else if (options.PruneAll)
        {
            grid.AddRow("[white]Action[/]", "[red]DELETE ALL BACKUPS[/]");
        }
    }

    private static void AddUpdatePackagesRows(Grid grid, Options options)
    {
        grid.AddRow("[white]Transitive Deps[/]", $"[cyan1]{(options.IncludeTransitive ? "Yes" : "No")}[/]");
        grid.AddRow("[white]Pre-release[/]", $"[cyan1]{(options.IncludePrerelease ? "Yes" : "No")}[/]");
        grid.AddRow("[white]Dry Run[/]", $"[cyan1]{(options.DryRun ? "Yes" : "No")}[/]");
    }

    private static void AddUnifyPropsRows(Grid grid)
    {
        grid.AddRow("[white]Operation[/]", "[cyan1]Unify Directory.Build.props (Properties & Usings)[/]");
    }

    private bool AskConfirmation()
    {
        return _console.AskConfirmation("Proceed?");
    }

    /// <summary>
    /// Configures options based on the selected action and determines if solution path discovery is needed.
    /// </summary>
    private (bool NeedsPath, bool EarlyReturn) ConfigureActionOptions(
        string action,
        EnvironmentContext context,
        Options options)
    {
        // Fast-track migration with intelligent defaults
        if (action.StartsWith("🚀 Fast-Track") || action.StartsWith("⚡️ Migrate"))
        {
            options.SolutionFileDir = context.Solutions.FirstOrDefault() ?? context.Directory;
            options.OutputDir = options.SolutionFileDir;
            options.ConflictStrategy = ConflictStrategy.Highest;
            options.BackupDir = ".";

            if (action.Contains("Review Conflicts"))
            {
                options.InteractiveConflicts = true;
            }

            _console.WriteLine();
            _console.WriteMissionStatus(0);
            return (false, false);
        }

        // Map actions to option flags
        if (action.Contains("Analyze"))
        {
            options.Analyze = true;
        }
        else if (action.Contains("Security Audit"))
        {
            options.Analyze = true;
            options.AuditSecurity = true;
            options.IncludeTransitive = true;
        }
        else if (action.Contains("Rollback"))
        {
            options.Rollback = true;
        }
        else if (action.Contains("Batch"))
        {
            AskBatchOptions(options);
            return (false, true); // Early return
        }
        else if (action.Contains("Manage Backups"))
        {
            AskBackupManagementOptions(options);
            return (false, true); // Early return
        }
        else if (action.Contains("Update NuGet packages"))
        {
            options.UpdatePackages = true;
        }
        else if (action.Contains("Unify Directory.Build.props"))
        {
            options.UnifyProps = true;
        }

        return (true, false); // Needs path discovery
    }

    private static string EscapeMarkup(string text) => Markup.Escape(text);
}
