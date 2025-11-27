using CommandLine;
using CommandLine.Text;

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
        };

    /// <summary>
    /// Validates the command-line options for logical consistency.
    /// Throws ArgumentException if validation fails.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when options are invalid.</exception>
    public void Validate()
    {
        if (Analyze)
        {
            // Analyze mode has different validation rules
            if (DryRun)
            {
                throw new ArgumentException("--analyze cannot be used with --dry-run.");
            }

            if (Rollback)
            {
                throw new ArgumentException("--analyze cannot be used with --rollback.");
            }

            // Other migration options are ignored in analyze mode
            return;
        }

        if (Rollback)
        {
            // Rollback mode has different validation rules
            if (DryRun)
            {
                throw new ArgumentException("--rollback cannot be used with --dry-run.");
            }

            if (string.IsNullOrWhiteSpace(BackupDir))
            {
                throw new ArgumentException("backup-dir must be specified for rollback.");
            }

            // Other migration options are ignored in rollback mode
            return;
        }

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
            Console.WriteLine("Both solution and project directories are included, will use solution file as source." +
                              "\r\nWill ignore the project file specified.");
    }
}
