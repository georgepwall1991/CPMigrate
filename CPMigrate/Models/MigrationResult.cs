using CPMigrate.Fixers;

namespace CPMigrate.Models;

/// <summary>
/// Result of a CPM migration operation.
/// </summary>
public class MigrationResult
{
    /// <summary>
    /// Number of projects that were processed.
    /// </summary>
    public int ProjectsProcessed { get; init; }

    /// <summary>
    /// Number of unique packages found across all projects.
    /// </summary>
    public int PackagesCentralized { get; init; }

    /// <summary>
    /// Number of version conflicts that were detected and resolved.
    /// </summary>
    public int ConflictsResolved { get; init; }

    /// <summary>
    /// Path to the generated Directory.Packages.props file.
    /// </summary>
    public string PropsFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Path to the backup directory, if backups were created.
    /// </summary>
    public string? BackupPath { get; init; }

    /// <summary>
    /// Whether this was a dry-run (no files modified).
    /// </summary>
    public bool WasDryRun { get; init; }

    /// <summary>
    /// Exit code for the operation.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Analysis report, when run in analyze mode.
    /// </summary>
    public AnalysisReport? AnalysisReport { get; init; }

    /// <summary>
    /// Fix report, when analyze mode runs with --fix or --fix-dry-run.
    /// </summary>
    public FixReport? FixReport { get; init; }

    /// <summary>
    /// The package references the analysis was built from. Reporters that need to resolve a
    /// finding back to a project file (SARIF locations, for example) use this; analyzer issues
    /// themselves only carry project names.
    /// </summary>
    public ProjectPackageInfo? PackageInfo { get; init; }

    /// <summary>
    /// The directory the scan was rooted at, used to make reported file paths relative.
    /// </summary>
    public string? BasePath { get; init; }
}
