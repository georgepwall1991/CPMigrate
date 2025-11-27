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
}
