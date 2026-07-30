using CommandLine;
using CommandLine.Text;
using CPMigrate.Services;
using CPMigrate.Services.Update;

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
    Fail,
}

/// <summary>
/// The lowest finding severity that makes <c>--analyze</c> fail the build.
///
/// The values deliberately mirror <see cref="Models.AnalysisSeverity"/> so they compare directly,
/// with <see cref="Never"/> sitting above every real severity — nothing can reach it, which is
/// exactly what "report but do not gate" means.
/// </summary>
public enum FailOnSeverity
{
    /// <summary>Fail on any finding. The default, and CPMigrate's behaviour before 3.8.0.</summary>
    Info = 0,

    /// <summary>Fail on Low findings and worse.</summary>
    Low = 1,

    /// <summary>Fail on Moderate findings and worse.</summary>
    Moderate = 2,

    /// <summary>Fail on High and Critical findings.</summary>
    High = 3,

    /// <summary>Fail only on Critical findings.</summary>
    Critical = 4,

    /// <summary>Never fail on findings. They are still reported in full.</summary>
    Never = 5,
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
    public const int TestFailure = 7;

    /// <summary>
    /// A requested scan did not complete, so the findings are incomplete. Distinct from
    /// <see cref="AnalysisIssuesFound"/> — nothing was necessarily found wrong; the point is that
    /// part of the codebase went unexamined, and a gate must not read that as clean.
    /// </summary>
    public const int IncompleteAnalysis = 8;
}

/// <summary>
/// Configuration options for CPMigrate CLI tool.
/// Handles command-line argument parsing and validation.
/// </summary>
public class Options
{
    /// <summary>
    /// Default baseline file name. Dot-prefixed and repository-local, because a baseline is a record
    /// of accepted debt that belongs in version control next to the code it describes.
    /// </summary>
    public const string BaselineDefaultFileName = ".cpmigrate-baseline.json";


    [Option(
        's',
        "solution",
        HelpText = "Path to a .sln/.slnx file, or a directory containing one. When omitted, CPMigrate uses the current directory.",
        Required = false
    )]
    public string SolutionFileDir { get; set; } = string.Empty;

    [Option(
        'p',
        "project",
        HelpText = "Path to a specific project file, or a directory containing one project (.csproj, .fsproj, .vbproj)."
    )]
    public string ProjectFileDir { get; set; } = string.Empty;

    [Option('o', "output-dir", HelpText = "The props file output directory.", Default = ".")]
    public string OutputDir { get; set; } = ".";

    [Option(
        'k',
        "keep-attrs",
        Default = false,
        HelpText = "Keeps the 'Version' attribute in the project files."
    )]
    public bool KeepAttributes { get; set; }

    [Option(
        'n',
        "no-backup",
        Default = false,
        HelpText = "Disables the default backup option.",
        Required = false
    )]
    public bool NoBackup { get; set; }

    [Option(
        "backup-dir",
        Default = ".",
        HelpText = "The backup directory for project files about to be changed."
    )]
    public string BackupDir { get; set; } = ".";

    [Option(
        "add-gitignore",
        Default = false,
        HelpText = "Adds the backup directory to .gitignore file. Creates one if not present."
    )]
    public bool AddBackupToGitignore { get; set; }

    [Option(
        "gitignore-dir",
        Default = ".",
        HelpText = "The directory for .gitignore file if there isn't one existing."
    )]
    public string GitignoreDir { get; set; } = string.Empty;

    [Option(
        'd',
        "dry-run",
        Default = false,
        HelpText = "Preview changes without modifying any files."
    )]
    public bool DryRun { get; set; }

    [Option(
        "merge",
        Default = false,
        HelpText = "Merge into existing Directory.Packages.props instead of failing if it already exists."
    )]
    public bool MergeExisting { get; set; }

    [Option(
        "conflict-strategy",
        Default = ConflictStrategy.Highest,
        HelpText = "How to handle version conflicts: Highest (default), Lowest, or Fail."
    )]
    public ConflictStrategy ConflictStrategy { get; set; }

    [Option(
        'r',
        "rollback",
        Default = false,
        HelpText = "Restore project files from most recent backup and remove Directory.Packages.props."
    )]
    public bool Rollback { get; set; }

    [Option(
        'a',
        "analyze",
        Default = false,
        HelpText = "Analyze packages for issues (version inconsistencies, duplicates, redundant references) without modifying files."
    )]
    public bool Analyze { get; set; }

    [Option(
        "transitive",
        Default = false,
        HelpText = "Include transitive dependencies in analysis and migration suggestions (requires 'dotnet restore')."
    )]
    public bool IncludeTransitive { get; set; }

    [Option(
        "audit",
        Default = false,
        HelpText = "Perform security audit for known vulnerabilities (requires 'dotnet restore')."
    )]
    public bool AuditSecurity { get; set; }

    [Option(
        "outdated",
        Default = false,
        HelpText = "Include outdated package checks (requires --analyze)."
    )]
    public bool AnalyzeOutdated { get; set; }

    [Option(
        "deprecated",
        Default = false,
        HelpText = "Include deprecated package checks (requires --analyze)."
    )]
    public bool AnalyzeDeprecated { get; set; }

    [Option(
        "fail-on",
        Default = FailOnSeverity.Info,
        HelpText = "Lowest finding severity that fails the build: Info (default, any finding), Low, "
            + "Moderate, High, Critical, or Never to report without gating. Does not suppress "
            + "exit 8 for an incomplete scan."
    )]
    public FailOnSeverity FailOn { get; set; } = FailOnSeverity.Info;

    [Option(
        "explain",
        HelpText = "Explain a rule and exit: pass a rule ID (e.g. VersionInconsistency) or 'all' to "
            + "list every rule. Rule IDs appear in JSON issueCode and SARIF ruleId."
    )]
    public string? Explain { get; set; }

    [Option(
        "max-parallelism",
        HelpText = "Maximum projects scanned at once. Defaults to the processor count, capped at 8 "
            + "because each scan shells out to 'dotnet package list'. Use 1 to scan serially."
    )]
    public int? MaxParallelism { get; set; }

    [Option(
        "completions",
        HelpText = "Print a shell completion script and exit: Bash, Zsh, Fish, or PowerShell. "
            + "Generated from the option list, so it cannot go stale."
    )]
    public CompletionShell? Completions { get; set; }

    [Option(
        "baseline",
        HelpText = "Path to a baseline file of accepted findings. Findings it records are still "
            + "reported but do not fail the build, so a repository with existing debt can gate on "
            + "new problems only. Requires a path; set \"baseline\" in .cpmigrate.json to apply it "
            + "to every run."
    )]
    public string? Baseline { get; set; }

    [Option(
        "write-baseline",
        Default = false,
        HelpText = "Record the current findings as the accepted baseline instead of gating on them, "
            + "then exit. Writes to --baseline when given, otherwise " + BaselineDefaultFileName + "."
    )]
    public bool WriteBaseline { get; set; }

    [Option(
        'i',
        "interactive",
        Default = false,
        HelpText = "Run in interactive wizard mode with guided prompts."
    )]
    public bool Interactive { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Output Formatting
    // ═══════════════════════════════════════════════════════════════════════

    [Option(
        "output",
        Default = OutputFormat.Terminal,
        HelpText = "Output format: Terminal (default), Json for CI/CD integration, Sarif for GitHub "
            + "code scanning, or Markdown for a CI job summary or PR comment. Sarif and Markdown "
            + "require --analyze."
    )]
    public OutputFormat Output { get; set; }

    [Option(
        "output-file",
        HelpText = "Write output to file instead of stdout (only applies to Json and Sarif output)."
    )]
    public string? OutputFile { get; set; }

    [Option(
        'q',
        "quiet",
        Default = false,
        HelpText = "Suppress non-essential output (progress bars, spinners). Useful for scripts."
    )]
    public bool Quiet { get; set; }

    [Option(
        'v',
        "verbose",
        Default = false,
        HelpText = "Enable verbose diagnostic logging to a file (cpmigrate.log in current directory)."
    )]
    public bool Verbose { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Batch Processing
    // ═══════════════════════════════════════════════════════════════════════

    [Option("batch", HelpText = "Scan directory recursively for .sln files and process each.")]
    public string? BatchDir { get; set; }

    [Option(
        "batch-parallel",
        Default = false,
        HelpText = "Process solutions in parallel (default: sequential)."
    )]
    public bool BatchParallel { get; set; }

    [Option(
        "batch-continue",
        Default = false,
        HelpText = "Continue processing even if one solution fails."
    )]
    public bool BatchContinue { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Backup Management
    // ═══════════════════════════════════════════════════════════════════════

    [Option(
        "prune-backups",
        Default = false,
        HelpText = "Delete old backups, keeping only the most recent based on --retention."
    )]
    public bool PruneBackups { get; set; }

    [Option(
        "prune-all",
        Default = false,
        HelpText = "Delete ALL backups (requires confirmation unless --quiet is set)."
    )]
    public bool PruneAll { get; set; }

    [Option(
        "retention",
        Default = 5,
        HelpText = "Number of backups to keep when pruning (default: 5, 0 = keep all)."
    )]
    public int Retention { get; set; }

    [Option(
        "list-backups",
        Default = false,
        HelpText = "List all available backups with timestamps and file counts."
    )]
    public bool ListBackups { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Conflict Resolution
    // ═══════════════════════════════════════════════════════════════════════

    [Option(
        "interactive-conflicts",
        Default = false,
        HelpText = "Prompt for each version conflict instead of auto-resolving."
    )]
    public bool InteractiveConflicts { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.5 Options - Directory.Build.props Unification
    // ═══════════════════════════════════════════════════════════════════════

    [Option(
        "unify-props",
        Default = false,
        HelpText = "Migrate common properties from projects to Directory.Build.props."
    )]
    public bool UnifyProps { get; set; }

    [Option("force", Default = false, HelpText = "Force operations without confirmation prompts.")]
    public bool Force { get; set; }

    [Option(
        "doctor",
        Default = false,
        HelpText = "Diagnose your environment: SDK version, NuGet connectivity, workspace, config, and git status."
    )]
    public bool Doctor { get; set; }

    [Option(
        "init",
        Default = false,
        HelpText = "Create a .cpmigrate.json config file with team defaults. Prompts interactively, or writes sensible defaults in CI."
    )]
    public bool Init { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v2.0 Options - Analysis & Auto-Fix
    // ═══════════════════════════════════════════════════════════════════════

    [Option(
        "fix",
        Default = false,
        HelpText = "Apply automatic fixes for detected issues (use with --analyze)."
    )]
    public bool Fix { get; set; }

    [Option(
        "fix-dry-run",
        Default = false,
        HelpText = "Show what --fix would change without modifying files."
    )]
    public bool FixDryRun { get; set; }

    [Option(
        "update",
        Default = false,
        HelpText = "Check for and install the latest version of CPMigrate."
    )]
    public bool Update { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v3.0 Options - Package Updates
    // ═══════════════════════════════════════════════════════════════════════

    [Option(
        "update-packages",
        Default = false,
        HelpText = "Update all packages to latest versions, run tests, rollback on failure."
    )]
    public bool UpdatePackages { get; set; }

    [Option(
        "include-prerelease",
        Default = false,
        HelpText = "Include pre-release versions when updating packages."
    )]
    public bool IncludePrerelease { get; set; }

    // ═══════════════════════════════════════════════════════════════════════
    // v3.6 Options - Bisecting Updates
    // ═══════════════════════════════════════════════════════════════════════

    [Option(
        "bisect",
        Default = false,
        HelpText = "On test failure, bisect the update set and keep the largest subset that stays green "
            + "instead of rolling everything back."
    )]
    public bool Bisect { get; set; }

    // Nullable rather than defaulted: it keeps "the user asked for a budget" distinguishable from "nobody
    // said", which the validation below relies on, and stops the value being echoed into every --help example.
    [Option(
        "bisect-budget",
        HelpText = "Maximum restore+test cycles a bisection may spend before holding back what is left. "
            + "Defaults to 16."
    )]
    public int? BisectBudget { get; set; }

    /// <summary>
    /// The bisect run budget to use, falling back to the strategy default when unspecified.
    /// </summary>
    public int EffectiveBisectBudget => BisectBudget ?? BisectSearchStrategy.DefaultBudget;

    [Option(
        "bisect-test-filter",
        HelpText = "dotnet test --filter expression used for each bisection probe, to narrow the search to a "
            + "fast subset of the suite."
    )]
    public string? BisectTestFilter { get; set; }

    [Option("only", HelpText = "Comma-separated package IDs to restrict --update-packages to.")]
    public string? Only { get; set; }

    /// <summary>
    /// Splits <see cref="Only"/> into package IDs, or returns null when the whole set is in play.
    /// </summary>
    public IReadOnlyList<string>? ParseOnlyPackages()
    {
        if (string.IsNullOrWhiteSpace(Only))
        {
            return null;
        }

        var names = Only.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .ToList();

        return names.Count > 0 ? names : null;
    }

    public bool HasExplicitSolutionPath => !string.IsNullOrWhiteSpace(SolutionFileDir);

    public bool HasExplicitProjectPath => !string.IsNullOrWhiteSpace(ProjectFileDir);

    public string GetDiscoveryTargetPath()
    {
        if (HasExplicitProjectPath)
        {
            return ProjectFileDir;
        }

        if (HasExplicitSolutionPath)
        {
            return SolutionFileDir;
        }

        return ".";
    }

    public string GetConfigSearchStartDirectory()
    {
        if (!string.IsNullOrWhiteSpace(BatchDir))
        {
            return ResolvePathToDirectory(BatchDir);
        }

        return ResolvePathToDirectory(GetDiscoveryTargetPath());
    }

    private static string ResolvePathToDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ".";
        }

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        var directory = Path.GetDirectoryName(fullPath);
        return string.IsNullOrWhiteSpace(directory) ? "." : directory;
    }

    [Usage(ApplicationAlias = "cpmigrate")]
    public static IEnumerable<Example> Examples =>
        new List<Example>()
        {
            new("Default behaviour", new Options { }),
            new(
                "Convert only one project",
                new Options { ProjectFileDir = Path.Combine("path", "to", "project.csproj") }
            ),
            new(
                "Specify the output directory, generates '../upDir/Directory.Packages.props'",
                new Options { OutputDir = Path.Combine("..", "upDir") }
            ),
            new("Preview changes without modifying files", new Options { DryRun = true }),
            new(
                "Fail if version conflicts are detected",
                new Options { ConflictStrategy = ConflictStrategy.Fail }
            ),
            new("Analyze packages for issues without migrating", new Options { Analyze = true }),
            new("Run in interactive wizard mode", new Options { Interactive = true }),
            new(
                "Merge with existing Directory.Packages.props",
                new Options { MergeExisting = true }
            ),
            // v2.0 examples
            new("Output JSON for CI/CD integration", new Options { Output = OutputFormat.Json }),
            new(
                "Emit SARIF for GitHub code scanning",
                new Options
                {
                    Analyze = true,
                    Output = OutputFormat.Sarif,
                    OutputFile = "cpmigrate.sarif",
                }
            ),
            new(
                "Batch migrate all solutions in a directory",
                new Options { BatchDir = Path.Combine("path", "to", "repo") }
            ),
            new("Analyze and auto-fix issues", new Options { Analyze = true, Fix = true }),
            new("Diagnose your environment", new Options { Doctor = true }),
            new("Create a .cpmigrate.json config", new Options { Init = true }),
            new(
                "Analyze and include outdated/deprecated package checks",
                new Options
                {
                    Analyze = true,
                    AnalyzeOutdated = true,
                    AnalyzeDeprecated = true,
                }
            ),
            new(
                "Prune old backups, keeping last 3",
                new Options { PruneBackups = true, Retention = 3 }
            ),
        };

    /// <summary>
    /// The baseline file this run should read or write, resolving the default when the flag was
    /// given without a path.
    /// </summary>
    public string ResolveBaselinePath()
    {
        return string.IsNullOrWhiteSpace(Baseline) ? BaselineDefaultFileName : Baseline;
    }

    /// <summary>
    /// True when the run should match findings against a baseline. Writing one is a separate mode:
    /// it would be circular to suppress the findings being recorded.
    /// </summary>
    public bool UsesBaseline()
    {
        return !WriteBaseline && !string.IsNullOrWhiteSpace(Baseline);
    }

    /// <summary>
    /// Produces the options for one solution inside a <c>--batch</c> run.
    ///
    /// Copies everything and overrides only what must differ per solution. The direction matters:
    /// this used to be an explicit allow-list, so every option added afterwards was silently
    /// dropped — a batch run ignored <c>--audit</c>, <c>--outdated</c>, <c>--deprecated</c>, and
    /// <c>--transitive</c>, meaning a monorepo security scan reported no vulnerabilities because it
    /// never looked for any. Copy-then-override fails safe: a new option propagates unless someone
    /// deliberately excludes it.
    /// </summary>
    /// <summary>
    /// How many projects to scan at once.
    ///
    /// Each project scan shells out to <c>dotnet package list</c>, so the useful range is bounded by
    /// what the machine and the feed will tolerate rather than by CPU: the work is almost entirely
    /// waiting. Capped at 8 by default because beyond that the NuGet feed starts rate-limiting, which
    /// makes the scan slower and noisier rather than faster.
    /// </summary>
    public int ResolveScanParallelism()
    {
        const int defaultCap = 8;

        if (MaxParallelism.HasValue)
        {
            return Math.Max(1, MaxParallelism.Value);
        }

        return Math.Clamp(Environment.ProcessorCount, 1, defaultCap);
    }

    /// <param name="solutionDir">Directory of the solution being processed.</param>
    /// <param name="backupDirName">Per-solution backup directory name, so parallel runs cannot collide.</param>
    internal Options CloneForBatchSolution(string solutionDir, string backupDirName)
    {
        var clone = (Options)MemberwiseClone();

        clone.SolutionFileDir = solutionDir;
        clone.OutputDir = solutionDir;
        clone.GitignoreDir = solutionDir;
        clone.BackupDir = Path.Combine(solutionDir, backupDirName);

        // Batch operates per solution, so a project filter from the outer invocation cannot apply.
        clone.ProjectFileDir = string.Empty;

        // Individual solution output would drown the batch summary.
        clone.Quiet = true;

        // Modes the batch driver owns and must not delegate to a solution run.
        clone.BatchDir = string.Empty;
        clone.Rollback = false;
        clone.Interactive = false;

        return clone;
    }

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

        // Update-packages mode exits early after validation
        if (ValidateUpdatePackagesOptions())
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
        if (!string.IsNullOrEmpty(OutputFile) && !Output.IsMachineReadable())
        {
            throw new ArgumentException(
                "--output-file can only be used with --output Json or --output Sarif."
            );
        }

        if (Output.IsMachineReadable() && Interactive)
        {
            throw new ArgumentException(
                $"--output {Output} cannot be used with --interactive mode."
            );
        }

        if (Output.IsMachineReadable() && InteractiveConflicts)
        {
            throw new ArgumentException(
                $"--output {Output} cannot be used with --interactive-conflicts."
            );
        }

        ValidateReportingContract();
    }

    /// <summary>
    /// A baseline records analyzer findings, so it only makes sense for a command that produces
    /// them. Silently ignoring the flag would leave someone believing debt was accepted when it
    /// never was.
    /// </summary>
    private void ValidateBaselineOptions()
    {
        if (!WriteBaseline && string.IsNullOrWhiteSpace(Baseline))
        {
            return;
        }

        if (!Analyze)
        {
            throw new ArgumentException(
                "--baseline and --write-baseline require --analyze; a baseline records analyzer findings."
            );
        }

        var conflictingMode = FindModeInsteadOfAnalysis();
        if (conflictingMode is not null)
        {
            // These run instead of an analysis, so the baseline would be silently ignored — while a
            // mutating operation went ahead.
            throw new ArgumentException(
                $"--baseline and --write-baseline cannot be combined with {conflictingMode}, which "
                    + "runs instead of an analysis. Run the analysis as a separate step."
            );
        }

        if (WriteBaseline && !string.IsNullOrEmpty(BatchDir))
        {
            // Every solution in the batch would write the same file: sequentially the last one wins,
            // in parallel they race. Either way the resulting baseline covers one solution while
            // claiming to cover the repository, so the next batch run treats the rest as new debt.
            throw new ArgumentException(
                "--write-baseline cannot be combined with --batch, because each solution would "
                    + "overwrite the same file. Record a baseline per solution with -s, or record "
                    + "one for the whole repository from a single run."
            );
        }

        if (WriteBaseline && Fix)
        {
            // The findings would be recorded from the pre-fix tree, immediately accepting debt the
            // same run just repaired.
            throw new ArgumentException(
                "--write-baseline cannot be combined with --fix. Record the baseline first, or fix "
                    + "and then record what remains."
            );
        }
    }

    /// <summary>
    /// SARIF describes analyzer findings, so it only makes sense for commands that produce them.
    /// Rejecting it elsewhere is friendlier than emitting an empty, valid-but-useless log that a
    /// code-scanning upload would silently accept.
    ///
    /// Public and called separately from <see cref="Validate"/> because several modes
    /// (<c>--update</c>, <c>--interactive</c>, <c>--unify-props</c>) are dispatched before
    /// per-command validation runs. Without an early check, <c>--update --output Sarif</c> would
    /// perform a real self-update and emit no SARIF at all.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when SARIF is requested for an unsupported mode.</exception>
    /// <summary>
    /// Checks the option combinations that must be rejected before <c>CommandRouter</c> dispatches
    /// a mode. Several modes (<c>--update</c>, <c>--interactive</c>, <c>--unify-props</c>, pruning)
    /// run ahead of per-command validation, so a contract enforced only in <see cref="Validate"/>
    /// would be bypassed — <c>--update --analyze --write-baseline</c> would perform a real
    /// self-update and never write or reject anything.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the combination is unsupported.</exception>
    public void ValidateReportingContract()
    {
        ValidateSarifOptions();
        ValidateMarkdownOptions();
        ValidateBaselineOptions();
    }

    /// <summary>
    /// Markdown reports analyzer findings, so like SARIF it only makes sense for a command that
    /// produces them.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when Markdown is requested for an unsupported mode.</exception>
    public void ValidateMarkdownOptions()
    {
        if (Output != OutputFormat.Markdown)
        {
            return;
        }

        if (!Analyze)
        {
            throw new ArgumentException(
                "--output Markdown requires --analyze; the report describes analyzer findings."
            );
        }

        if (!string.IsNullOrEmpty(BatchDir))
        {
            // Batch runs each solution through a silent console and aggregates into a BatchResult,
            // which this report has no shape for — the command would emit nothing at all.
            throw new ArgumentException(
                "--output Markdown cannot be used with --batch; run one solution at a time, or use "
                    + "--output Json."
            );
        }

        var conflictingMode = FindModeInsteadOfAnalysis();
        if (conflictingMode is not null)
        {
            throw new ArgumentException(
                $"--output Markdown cannot be combined with {conflictingMode}, which runs instead "
                    + "of an analysis."
            );
        }
    }

    public void ValidateSarifOptions()
    {
        if (Output != OutputFormat.Sarif)
        {
            return;
        }

        if (!Analyze)
        {
            throw new ArgumentException(
                "--output Sarif requires --analyze; SARIF only carries analyzer findings."
            );
        }

        // Requiring --analyze is not sufficient: these modes are dispatched ahead of it, so
        // passing both would run the other operation and emit no SARIF at all. Name each one
        // rather than inferring, so a mode added later fails loudly here instead of silently
        // producing an empty log.
        if (!string.IsNullOrEmpty(BatchDir))
        {
            throw new ArgumentException(
                "--output Sarif cannot be used with --batch; run one solution at a time, or use --output Json."
            );
        }

        var conflictingMode = FindModeInsteadOfAnalysis();
        if (conflictingMode is not null)
        {
            throw new ArgumentException(
                $"--output Sarif cannot be combined with {conflictingMode}; it reports analyzer "
                    + "findings for a single solution. Run the analysis as a separate step."
            );
        }

        if (Fix)
        {
            // The report describes the tree as it was before the fixes were written, so uploading
            // it would annotate findings that no longer exist. --fix-dry-run changes nothing and
            // is therefore fine.
            throw new ArgumentException(
                "--output Sarif cannot be combined with --fix, because the findings would describe "
                    + "the projects as they were before the fixes. Use --fix-dry-run, or re-run "
                    + "the analysis after fixing."
            );
        }
    }

    /// <summary>
    /// Returns the flag naming a mode that runs instead of an analysis, or null when none is set.
    ///
    /// <c>CommandRouter</c> dispatches these ahead of analysis, so any feature that reports on
    /// analyzer findings — SARIF output, baselines — is silently skipped when one is present. Naming
    /// them explicitly means a mode added later fails loudly here rather than quietly doing nothing.
    /// </summary>
    private string? FindModeInsteadOfAnalysis()
    {
        if (Update)
        {
            return "--update";
        }

        if (UpdatePackages)
        {
            return "--update-packages";
        }

        if (Interactive)
        {
            return "--interactive";
        }

        if (InteractiveConflicts)
        {
            return "--interactive-conflicts";
        }

        if (UnifyProps)
        {
            return "--unify-props";
        }

        if (Rollback)
        {
            return "--rollback";
        }

        if (PruneBackups)
        {
            return "--prune-backups";
        }

        if (PruneAll)
        {
            return "--prune-all";
        }

        if (ListBackups)
        {
            return "--list-backups";
        }

        return null;
    }

    /// <summary>
    /// Validates batch processing options.
    /// </summary>
    private void ValidateBatchOptions()
    {
        if (!string.IsNullOrEmpty(BatchDir))
        {
            if (!Directory.Exists(BatchDir))
            {
                throw new ArgumentException($"Batch directory does not exist: {BatchDir}");
            }

            if (HasExplicitSolutionPath)
            {
                throw new ArgumentException("--batch cannot be used with --solution.");
            }

            if (HasExplicitProjectPath)
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

        if (Output.IsMachineReadable() && !Force)
        {
            throw new ArgumentException(
                "--prune-backups and --prune-all require --force when --output Json is used."
            );
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
        if (AnalyzeOutdated && !Analyze)
        {
            throw new ArgumentException("--outdated requires --analyze.");
        }

        if (AnalyzeDeprecated && !Analyze)
        {
            throw new ArgumentException("--deprecated requires --analyze.");
        }

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

        if (Output.IsMachineReadable() && !Force)
        {
            throw new ArgumentException("--rollback requires --force when --output Json is used.");
        }

        return true;
    }

    /// <summary>
    /// Validates update-packages mode options.
    /// Returns true if update-packages mode is active (skip remaining validation).
    /// </summary>
    private bool ValidateUpdatePackagesOptions()
    {
        // Check --include-prerelease dependency before early return
        if (IncludePrerelease && !UpdatePackages)
        {
            throw new ArgumentException("--include-prerelease requires --update-packages.");
        }

        ValidateBisectOptions();

        if (!UpdatePackages)
        {
            return false;
        }

        if (Rollback)
        {
            throw new ArgumentException("--update-packages cannot be used with --rollback.");
        }

        if (Analyze)
        {
            throw new ArgumentException("--update-packages cannot be used with --analyze.");
        }

        if (!string.IsNullOrEmpty(BatchDir))
        {
            throw new ArgumentException("--update-packages cannot be used with --batch.");
        }

        if (UnifyProps)
        {
            throw new ArgumentException("--update-packages cannot be used with --unify-props.");
        }

        if (Bisect && DryRun)
        {
            throw new ArgumentException(
                "--bisect cannot be used with --dry-run. Bisection has to run tests against real changes."
            );
        }

        return true;
    }

    /// <summary>
    /// Validates the bisect option family. Runs before the --update-packages early return so a stray
    /// --bisect on an unrelated command is rejected rather than silently ignored.
    /// </summary>
    private void ValidateBisectOptions()
    {
        if (Bisect && !UpdatePackages)
        {
            throw new ArgumentException("--bisect requires --update-packages.");
        }

        if (!string.IsNullOrWhiteSpace(BisectTestFilter) && !Bisect)
        {
            throw new ArgumentException("--bisect-test-filter requires --bisect.");
        }

        if (BisectBudget.HasValue && !Bisect)
        {
            throw new ArgumentException("--bisect-budget requires --bisect.");
        }

        if (Bisect && EffectiveBisectBudget < 1)
        {
            throw new ArgumentException("--bisect-budget must be at least 1.");
        }

        if (!string.IsNullOrWhiteSpace(Only) && !UpdatePackages)
        {
            throw new ArgumentException("--only requires --update-packages.");
        }
    }

    /// <summary>
    /// Validates migration mode options (backup and gitignore settings).
    /// </summary>
    private void ValidateMigrationOptions()
    {
        if (NoBackup && AddBackupToGitignore)
        {
            throw new ArgumentException(
                "--add-gitignore cannot be used with --no-backup. "
                    + "A backup directory must exist to add it to .gitignore."
            );
        }

        if (!NoBackup && string.IsNullOrWhiteSpace(BackupDir))
        {
            throw new ArgumentException("backup-dir must be specified when backup is enabled.");
        }

        if (AddBackupToGitignore && string.IsNullOrWhiteSpace(GitignoreDir))
        {
            throw new ArgumentException(
                "gitignore-dir must be specified when add-gitignore is enabled."
            );
        }

        if (HasExplicitSolutionPath && HasExplicitProjectPath)
        {
            throw new ArgumentException(
                "Both --solution and --project were specified. Use only one: "
                    + "--solution to scan a .sln file, or --project to scan a specific project."
            );
        }
    }
}
