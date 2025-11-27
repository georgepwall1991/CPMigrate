namespace CPMigrate.Services;

/// <summary>
/// Implementation of interactive wizard mode for CPMigrate.
/// Guides users through options using Spectre.Console prompts.
/// </summary>
public class InteractiveService : IInteractiveService
{
    private readonly IConsoleService _console;

    private const string ModeMigrate = "Migrate to Central Package Management";
    private const string ModeAnalyze = "Analyze packages for issues";
    private const string ModeRollback = "Rollback a previous migration";

    private const string ConflictHighest = "Highest version (recommended)";
    private const string ConflictLowest = "Lowest version";
    private const string ConflictFail = "Fail on conflict";

    private const string EnterPathManually = "Enter path manually...";

    public InteractiveService(IConsoleService console)
    {
        _console = console;
    }

    /// <inheritdoc />
    public Options? RunWizard()
    {
        try
        {
            var options = new Options();

            // Step 1: Mode selection
            var mode = AskMode();
            ApplyMode(options, mode);

            // Step 2: Solution discovery
            var solutionPath = AskSolutionPath();
            if (solutionPath == null)
            {
                _console.Warning("No solution or project path provided. Cancelling.");
                return null;
            }
            options.SolutionFileDir = solutionPath;

            // Step 3: Mode-specific options
            switch (mode)
            {
                case ModeMigrate:
                    AskMigrationOptions(options);
                    break;
                case ModeRollback:
                    AskRollbackOptions(options);
                    break;
                // Analyze mode has no additional options
            }

            // Step 4: Summary and confirmation
            ShowSummary(options, mode);

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

    private string AskMode()
    {
        return _console.AskSelection(
            "What would you like to do?",
            new[] { ModeMigrate, ModeAnalyze, ModeRollback });
    }

    private static void ApplyMode(Options options, string mode)
    {
        switch (mode)
        {
            case ModeAnalyze:
                options.Analyze = true;
                break;
            case ModeRollback:
                options.Rollback = true;
                break;
        }
    }

    private string? AskSolutionPath()
    {
        // Auto-detect .sln files in current directory
        var currentDir = Directory.GetCurrentDirectory();
        var solutionFiles = Directory.GetFiles(currentDir, "*.sln", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToList();

        if (solutionFiles.Count == 0)
        {
            // No solution files found - ask for manual input
            _console.Warning("No .sln files found in current directory.");
            var path = _console.AskText("Enter solution or project directory path", ".");
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        // Add option to enter path manually
        var choices = solutionFiles.Concat(new[] { EnterPathManually }).ToList();

        var selection = _console.AskSelection(
            "Select a solution file",
            choices);

        if (selection == EnterPathManually)
        {
            var path = _console.AskText("Enter solution or project directory path", ".");
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        // Return the directory containing the selected solution
        return currentDir;
    }

    private void AskMigrationOptions(Options options)
    {
        // Conflict strategy
        var conflictChoice = _console.AskSelection(
            "Conflict resolution strategy?",
            new[] { ConflictHighest, ConflictLowest, ConflictFail });

        options.ConflictStrategy = conflictChoice switch
        {
            ConflictLowest => ConflictStrategy.Lowest,
            ConflictFail => ConflictStrategy.Fail,
            _ => ConflictStrategy.Highest
        };

        // Backup option
        var createBackup = _console.AskSelection(
            "Create backup before migration?",
            new[] { "Yes (recommended)", "No" });

        options.NoBackup = createBackup == "No";

        if (!options.NoBackup)
        {
            options.BackupDir = _console.AskText("Backup directory", "./cpm-backup");

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
    }

    private void AskRollbackOptions(Options options)
    {
        options.BackupDir = _console.AskText("Where is the backup located?", "./cpm-backup");
    }

    private void ShowSummary(Options options, string mode)
    {
        _console.WriteLine();
        _console.Separator();

        var modeLabel = mode switch
        {
            ModeMigrate => "MIGRATE",
            ModeAnalyze => "ANALYZE",
            ModeRollback => "ROLLBACK",
            _ => "UNKNOWN"
        };

        _console.WriteMarkup($"[cyan1][[>]] Ready to {modeLabel}[/]");
        _console.WriteLine();
        _console.WriteMarkup($"[white]  Solution/Project:[/] [dim]{options.SolutionFileDir}[/]");

        if (mode == ModeMigrate)
        {
            _console.WriteMarkup($"[white]  Conflict Strategy:[/] [dim]{options.ConflictStrategy}[/]");
            _console.WriteMarkup($"[white]  Backup:[/] [dim]{(options.NoBackup ? "No" : $"Yes ({options.BackupDir})")}[/]");
            _console.WriteMarkup($"[white]  Dry Run:[/] [dim]{(options.DryRun ? "Yes" : "No")}[/]");
            _console.WriteMarkup($"[white]  Keep Version Attrs:[/] [dim]{(options.KeepAttributes ? "Yes" : "No")}[/]");
        }
        else if (mode == ModeRollback)
        {
            _console.WriteMarkup($"[white]  Backup Location:[/] [dim]{options.BackupDir}[/]");
        }

        _console.WriteLine();
        _console.Separator();
    }

    private bool AskConfirmation()
    {
        return _console.AskConfirmation("Proceed?");
    }
}
