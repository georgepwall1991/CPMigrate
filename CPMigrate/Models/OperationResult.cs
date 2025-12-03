using System.Text.Json.Serialization;

namespace CPMigrate.Models;

/// <summary>
/// Unified result object for all CPMigrate operations, suitable for JSON serialization.
/// </summary>
public class OperationResult
{
    /// <summary>
    /// CPMigrate version that produced this result.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "2.0.0";

    /// <summary>
    /// The type of operation performed.
    /// </summary>
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = string.Empty;

    /// <summary>
    /// Whether the operation completed successfully.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// Exit code for the operation.
    /// </summary>
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    /// <summary>
    /// Summary statistics for the operation.
    /// </summary>
    [JsonPropertyName("summary")]
    public OperationSummary Summary { get; init; } = new();

    /// <summary>
    /// List of version conflicts detected and how they were resolved.
    /// </summary>
    [JsonPropertyName("conflicts")]
    public List<ConflictInfo> Conflicts { get; init; } = new();

    /// <summary>
    /// Analysis issues found (for analyze mode).
    /// </summary>
    [JsonPropertyName("analysisIssues")]
    public List<AnalysisIssueInfo> AnalysisIssues { get; init; } = new();

    /// <summary>
    /// Fixes applied (for analyze --fix mode).
    /// </summary>
    [JsonPropertyName("fixes")]
    public List<FixInfo> Fixes { get; init; } = new();

    /// <summary>
    /// Errors encountered during the operation.
    /// </summary>
    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = new();

    /// <summary>
    /// Warnings generated during the operation.
    /// </summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = new();

    /// <summary>
    /// Information about the generated props file.
    /// </summary>
    [JsonPropertyName("propsFile")]
    public PropsFileInfo? PropsFile { get; init; }

    /// <summary>
    /// Information about backup location.
    /// </summary>
    [JsonPropertyName("backup")]
    public BackupInfo? Backup { get; init; }

    /// <summary>
    /// Whether this was a dry-run (no files modified).
    /// </summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; init; }

    /// <summary>
    /// Timestamp when the operation completed.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");
}

/// <summary>
/// Summary statistics for an operation.
/// </summary>
public class OperationSummary
{
    [JsonPropertyName("projectsProcessed")]
    public int ProjectsProcessed { get; init; }

    [JsonPropertyName("packagesFound")]
    public int PackagesFound { get; init; }

    [JsonPropertyName("conflictsResolved")]
    public int ConflictsResolved { get; init; }

    [JsonPropertyName("issuesFound")]
    public int IssuesFound { get; init; }

    [JsonPropertyName("issuesFixed")]
    public int IssuesFixed { get; init; }

    [JsonPropertyName("filesModified")]
    public int FilesModified { get; init; }
}

/// <summary>
/// Information about a version conflict and its resolution.
/// </summary>
public class ConflictInfo
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = string.Empty;

    [JsonPropertyName("versions")]
    public List<VersionUsage> Versions { get; init; } = new();

    [JsonPropertyName("resolved")]
    public string Resolved { get; init; } = string.Empty;

    [JsonPropertyName("resolution")]
    public string Resolution { get; init; } = string.Empty;

    [JsonPropertyName("overridden")]
    public bool Overridden { get; init; }
}

/// <summary>
/// Information about version usage by projects.
/// </summary>
public class VersionUsage
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("projects")]
    public List<string> Projects { get; init; } = new();
}

/// <summary>
/// Analysis issue information for JSON output.
/// </summary>
public class AnalysisIssueInfo
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("package")]
    public string Package { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("affectedProjects")]
    public List<string> AffectedProjects { get; init; } = new();

    [JsonPropertyName("fixable")]
    public bool Fixable { get; init; }
}

/// <summary>
/// Information about a fix that was applied.
/// </summary>
public class FixInfo
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("package")]
    public string Package { get; init; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("applied")]
    public bool Applied { get; init; }
}

/// <summary>
/// Information about the generated props file.
/// </summary>
public class PropsFileInfo
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

/// <summary>
/// Information about backup location.
/// </summary>
public class BackupInfo
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("filesBackedUp")]
    public int FilesBackedUp { get; init; }
}
