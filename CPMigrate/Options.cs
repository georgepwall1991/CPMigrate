using CommandLine;
using CommandLine.Text;
using CPMigrate.Services;

namespace CPMigrate;

/// <summary>
/// Strategy for resolving version conflicts when the same package has different versions across projects.
/// </summary>
public enum ConflictStrategy
{
    /// <summary>Use the highest version found (default).</summary>
    Highest,
    /// <summary>Use the lowest version found.</summary>
    Lowest,
    /// <summary>Exit with error if conflicts are detected.</summary>
    Fail
}

/// <summary>
/// Exit codes returned by CPMigrate.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int ValidationError = 1;
    public const int FileOperationError = 2;
    public const int VersionConflict = 3;
    public const int NoProjectsFound = 4;
    public const int AnalysisIssuesFound = 5;
    public const int UnexpectedError = 6;
}

/// <summary>
/// Configuration options for CPMigrate CLI tool.
/// Handles command-line argument parsing and validation.
/// </summary>
public class Options
{
    [Option('s', "solution",
        HelpText = "Specifies the directory to search for .sln files. If this option is provided " +
                   "the project file location option will be ignored.", Required = false, Default = ".")]
    public string SolutionFileDir { get; set; } = string.Empty;

    [Option('p', "project", HelpText = "Specifies the directory to search for project files (.csproj, .fsproj, .vbproj).")]
    public string ProjectFileDir { get; set; } = string.Empty;

    [Option('o', "output-dir", HelpText = "The props file output directory.", Default = ".")]
    public string OutputDir { get; set; } = string.Empty;

    [Option('k', "keep-attrs", Default = false,
        HelpText = "Keeps the 'Version' attribute in the project files.")]
    public bool KeepAttributes { get; set; }

    [Option('n', "no-backup", Default = false,
        HelpText = "Disables the default backup option.", Required = false)]
    public bool NoBackup { get; set; }

    [Option("backup-dir", Default = ".",
        HelpText = "The backup directory for project files about to be changed.")]
    public string BackupDir { get; set; } = string.Empty;

    [Option("add-gitignore", Default = false,
        HelpText = "Adds the backup directory to .gitignore file. Creates one if not present.")]
    public bool AddBackupToGitignore { get; set; }

    [Option("gitignore-dir", Default = ".",
        HelpText = "The directory for .gitignore file if there isn't one existing.")]
    public string GitignoreDir { get; set; } = string.Empty;

    [Option('d', "dry-run", Default = false,
        HelpText = "Preview changes without modifying any files.")]
    public bool DryRun { get; set; }

    [Option("conflict-strategy", Default = ConflictStrategy.Highest,
        HelpText = "How to handle version conflicts: Highest (default), Lowest, or Fail.")]
    public ConflictStrategy ConflictStrategy { get; set; }

    [Option('r', "rollback", Default = false,
        HelpText = "Restore project files from most recent backup and remove Directory.Packages.props.")]
    public bool Rollback { get; set; }

    [Option('a', "analyze", Default = false,
        HelpText = "Analyze packages for issues (version inconsistencies, duplicates, redundant references) without modifying files.")]
    public bool Analyze { get; set; }

    [Option('i', "interactive", Default = false,
        HelpText = "Run in interactive wizard mode with guided prompts.")]
    public bool Interactive { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Output Formatting
    // ═══════════════════════════════════════════════════════════════════════

    [Option("output", Default = OutputFormat.Terminal,
        HelpText = "Output format: Terminal (default) or Json for CI/CD integration.")]
    public OutputFormat Output { get; set; }

    [Option("output-file",
        HelpText = "Write output to file instead of stdout (only applies to JSON output).")]
    public string? OutputFile { get; set; }

    [Option('q', "quiet", Default = false,
        HelpText = "Suppress non-essential output (progress bars, spinners). Useful for scripts.")]
    public bool Quiet { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Batch Processing
    // ═══════════════════════════════════════════════════════════════════════

    [Option("batch",
        HelpText = "Scan directory recursively for .sln files and process each.")]
    public string? BatchDir { get; set; }

    [Option("batch-parallel", Default = false,
        HelpText = "Process solutions in parallel (default: sequential).")]
    public bool BatchParallel { get; set; }

    [Option("batch-continue", Default = false,
        HelpText = "Continue processing even if one solution fails.")]
    public bool BatchContinue { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Backup Management
    // ═══════════════════════════════════════════════════════════════════════

    [Option("prune-backups", Default = false,
        HelpText = "Delete old backups, keeping only the most recent based on --retention.")]
    public bool PruneBackups { get; set; }

    [Option("prune-all", Default = false,
        HelpText = "Delete ALL backups (requires confirmation unless --quiet is set).")]
    public bool PruneAll { get; set; }

    [Option("retention", Default = 5,
        HelpText = "Number of backups to keep when pruning (default: 5, 0 = keep all).")]
    public int Retention { get; set; }

    [Option("list-backups", Default = false,
        HelpText = "List all available backups with timestamps and file counts.")]
    public bool ListBackups { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Conflict Resolution
    // ═══════════════════════════════════════════════════════════════════════

    [Option("interactive-conflicts", Default = false,
        HelpText = "Prompt for each version conflict instead of auto-resolving.")]
    public bool InteractiveConflicts { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Analysis & Auto-Fix
    // ═══════════════════════════════════════════════════════════════════════

    [Option("fix", Default = false,
        HelpText = "Apply automatic fixes for detected issues (use with --analyze).")]
    public bool Fix { get; set; }

    [Option("fix-dry-run", Default = false,
        HelpText = "Show what --fix would change without modifying files.")]
    public bool FixDryRun { get; set; }

    [Usage(ApplicationAlias = "cpmigrate")]
    public static IEnumerable<Example> Examples =>
        new List<Example>()
        {
            new("Default behaviour", new Options { }),
            new("Convert only one project",
                new Options { ProjectFileDir = Path.Combine("path", "to", "project.csproj") }),
            new("Specify the output directory, generates '../upDir/Directory.Packages.props'",
                new Options { OutputDir = Path.Combine("..", "upDir") }),
            new("Preview changes without modifying files",
                new Options { DryRun = true }),
            new("Fail if version conflicts are detected",
                new Options { ConflictStrategy = ConflictStrategy.Fail }),
            new("Analyze packages for issues without migrating",
                new Options { Analyze = true }),
            new("Run in interactive wizard mode",
                new Options { Interactive = true }),
            // v2.0 examples
            new("Output JSON for CI/CD integration",
                new Options { Output = OutputFormat.Json }),
            new("Batch migrate all solutions in a directory",
                new Options { BatchDir = Path.Combine("path", "to", "repo") }),
            new("Analyze and auto-fix issues",
                new Options { Analyze = true, Fix = true }),
            new("Prune old backups, keeping last 3",
                new Options { PruneBackups = true, Retention = 3 }),
        };

    /// <summary>
    /// Validates the command-line options for logical consistency.
    /// Throws ArgumentException if validation fails.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when options are invalid.</exception>
    public void Validate()
    {
        ValidateOutputOptions();
        ValidateBatchOptions();

        // Prune mode exits early after validation
        if (ValidatePruneOptions())
        {
            return;
        }

        ValidateFixOptions();

        // Analyze mode exits early after validation
        if (ValidateAnalyzeOptions())
        {
            return;
        }

        // Rollback mode exits early after validation
        if (ValidateRollbackOptions())
        {
            return;
        }

        ValidateMigrationOptions();
    }

    /// <summary>
    /// Validates output format options.
    /// </summary>
    private void ValidateOutputOptions()
    {
        if (!string.IsNullOrEmpty(OutputFile) && Output != OutputFormat.Json)
        {
            throw new ArgumentException("--output-file can only be used with --output Json.");
        }

        if (Output == OutputFormat.Json && Interactive)
        {
            throw new ArgumentException("--output Json cannot be used with --interactive mode.");
        }
    }

    /// <summary>
    /// Validates batch processing options.
    /// </summary>
    private void ValidateBatchOptions()
    {
        if (!string.IsNullOrEmpty(BatchDir))
        {
            if (!string.IsNullOrEmpty(SolutionFileDir) && SolutionFileDir != ".")
            {
                throw new ArgumentException("--batch cannot be used with --solution.");
            }

            if (!string.IsNullOrEmpty(ProjectFileDir))
            {
                throw new ArgumentException("--batch cannot be used with --project.");
            }

            if (Rollback)
            {
                throw new ArgumentException("--batch cannot be used with --rollback.");
            }
        }

        if (BatchParallel && string.IsNullOrEmpty(BatchDir))
        {
            throw new ArgumentException("--batch-parallel requires --batch.");
        }

        if (BatchContinue && string.IsNullOrEmpty(BatchDir))
        {
            throw new ArgumentException("--batch-continue requires --batch.");
        }
    }

    /// <summary>
    /// Validates backup pruning options.
    /// Returns true if prune mode is active (skip remaining validation).
    /// </summary>
    private bool ValidatePruneOptions()
    {
        if (!PruneBackups && !PruneAll)
        {
            return false;
        }

        if (PruneBackups && PruneAll)
        {
            throw new ArgumentException("--prune-backups and --prune-all cannot be used together.");
        }

        if (Retention < 0)
        {
            throw new ArgumentException("--retention must be 0 or greater.");
        }

        return true;
    }

    /// <summary>
    /// Validates fix/auto-repair options.
    /// </summary>
    private void ValidateFixOptions()
    {
        if (Fix && !Analyze)
        {
            throw new ArgumentException("--fix requires --analyze.");
        }

        if (FixDryRun && !Analyze)
        {
            throw new ArgumentException("--fix-dry-run requires --analyze.");
        }

        if (Fix && FixDryRun)
        {
            throw new ArgumentException("--fix and --fix-dry-run cannot be used together.");
        }
    }

    /// <summary>
    /// Validates analyze mode options.
    /// Returns true if analyze mode is active (skip remaining validation).
    /// </summary>
    private bool ValidateAnalyzeOptions()
    {
        if (!Analyze)
        {
            return false;
        }

        if (DryRun)
        {
            throw new ArgumentException("--analyze cannot be used with --dry-run.");
        }

        if (Rollback)
        {
            throw new ArgumentException("--analyze cannot be used with --rollback.");
        }

        return true;
    }

    /// <summary>
    /// Validates rollback mode options.
    /// Returns true if rollback mode is active (skip remaining validation).
    /// </summary>
    private bool ValidateRollbackOptions()
    {
        if (!Rollback)
        {
            return false;
        }

        if (DryRun)
        {
            throw new ArgumentException("--rollback cannot be used with --dry-run.");
        }

        if (string.IsNullOrWhiteSpace(BackupDir))
        {
            throw new ArgumentException("backup-dir must be specified for rollback.");
        }

        return true;
    }

    /// <summary>
    /// Validates migration mode options (backup and gitignore settings).
    /// </summary>
    private void ValidateMigrationOptions()
    {
        if (NoBackup && AddBackupToGitignore)
        {
            throw new ArgumentException("--add-gitignore cannot be used with --no-backup. " +
                "A backup directory must exist to add it to .gitignore.");
        }

        if (!NoBackup && string.IsNullOrWhiteSpace(BackupDir))
        {
            throw new ArgumentException("backup-dir must be specified when backup is enabled.");
        }

        if (AddBackupToGitignore && string.IsNullOrWhiteSpace(GitignoreDir))
        {
            throw new ArgumentException("gitignore-dir must be specified when add-gitignore is enabled.");
        }

        if (!string.IsNullOrEmpty(SolutionFileDir) && !string.IsNullOrWhiteSpace(ProjectFileDir))
        {
            Console.WriteLine("Both solution and project directories are included, will use solution file as source." +
                              "\r\nWill ignore the project file specified.");
        }
    }
}
