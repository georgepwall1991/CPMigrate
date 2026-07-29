using System.Text.Json.Serialization;

namespace CPMigrate.Models;

/// <summary>
/// Unified result object for all CPMigrate operations, suitable for JSON serialization.
/// </summary>
public class OperationResult
{
    /// <summary>
    /// JSON contract version for this payload.
    /// </summary>
    [JsonPropertyName("outputSchemaVersion")]
    public string OutputSchemaVersion { get; init; } = OutputMetadata.SchemaVersion;

    /// <summary>
    /// CPMigrate version that produced this result.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = OutputMetadata.CurrentVersion;

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
    /// Package update entries (for update-packages mode).
    /// </summary>
    [JsonPropertyName("packageUpdates")]
    public List<PackageUpdateInfo> PackageUpdates { get; init; } = new();

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

    /// <summary>
    /// The configured <c>--fail-on</c> threshold, so a consumer can tell a genuinely clean run from
    /// one whose findings were below the gate.
    /// </summary>
    [JsonPropertyName("failOnSeverity")]
    public string? FailOnSeverity { get; init; }

    /// <summary>
    /// Findings at or above the <c>--fail-on</c> threshold — the subset the exit code reflects.
    /// </summary>
    [JsonPropertyName("issuesAtOrAboveThreshold")]
    public int? IssuesAtOrAboveThreshold { get; init; }

    /// <summary>
    /// Findings still present after a <c>--fix</c> run wrote its changes, from a fresh scan of the
    /// modified tree. Null when no fixes were applied. <c>issuesFound</c> stays as the pre-fix count
    /// so it lines up with the <c>fixes</c> array.
    /// </summary>
    [JsonPropertyName("issuesRemainingAfterFixes")]
    public int? IssuesRemainingAfterFixes { get; init; }

    /// <summary>
    /// Severity of the worst finding, or null when nothing was found.
    /// </summary>
    [JsonPropertyName("highestSeverity")]
    public string? HighestSeverity { get; init; }

    /// <summary>
    /// Projects that could not be scanned. Non-zero means the findings are incomplete.
    /// </summary>
    [JsonPropertyName("scanFailures")]
    public int? ScanFailures { get; init; }

    /// <summary>
    /// Opt-in package queries (<c>--audit</c>/<c>--outdated</c>/<c>--deprecated</c>) that failed.
    /// </summary>
    [JsonPropertyName("deepScanFailures")]
    public int? DeepScanFailures { get; init; }

    [JsonPropertyName("issuesFixed")]
    public int IssuesFixed { get; init; }

    [JsonPropertyName("filesModified")]
    public int FilesModified { get; init; }

    [JsonPropertyName("packagesChecked")]
    public int? PackagesChecked { get; init; }

    [JsonPropertyName("packagesUpdated")]
    public int? PackagesUpdated { get; init; }

    [JsonPropertyName("packagesSkipped")]
    public int? PackagesSkipped { get; init; }

    [JsonPropertyName("transitivePackagesFound")]
    public int? TransitivePackagesFound { get; init; }

    [JsonPropertyName("transitivePackagesUpdated")]
    public int? TransitivePackagesUpdated { get; init; }

    [JsonPropertyName("testsPassed")]
    public bool? TestsPassed { get; init; }

    [JsonPropertyName("wasRolledBack")]
    public bool? WasRolledBack { get; init; }

    /// <summary>Accepted updates that did not survive verification and were left at their old version.</summary>
    [JsonPropertyName("packagesHeldBack")]
    public int? PackagesHeldBack { get; init; }

    /// <summary>Restore+test cycles executed during the run.</summary>
    [JsonPropertyName("verificationRuns")]
    public int? VerificationRuns { get; init; }

    /// <summary>Whether bisection stopped early on its run budget.</summary>
    [JsonPropertyName("bisectBudgetExhausted")]
    public bool? BisectBudgetExhausted { get; init; }
}

/// <summary>
/// Analysis issue information for JSON output.
/// </summary>
public class AnalysisIssueInfo
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("issueCode")]
    public string IssueCode { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("package")]
    public string Package { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("affectedProjects")]
    public List<string> AffectedProjects { get; init; } = new();

    [JsonPropertyName("fixable")]
    public bool Fixable { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
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
/// Information about a single package update candidate/application.
/// </summary>
public class PackageUpdateInfo
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = string.Empty;

    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; init; } = string.Empty;

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; init; } = string.Empty;

    [JsonPropertyName("isMajorUpdate")]
    public bool IsMajorUpdate { get; init; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("transitive")]
    public bool Transitive { get; init; }

    /// <summary>
    /// Whether this update was excluded because it broke verification and was left at its old version.
    /// </summary>
    [JsonPropertyName("heldBack")]
    public bool HeldBack { get; init; }
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
