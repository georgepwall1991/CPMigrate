using System.Text.Json.Serialization;

namespace CPMigrate.Models;

/// <summary>
/// Result of a batch operation across multiple solutions.
/// </summary>
public partial class BatchResult
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
    public string Operation { get; init; } = "batch-migrate";

    /// <summary>
    /// Overall success status (true only if all solutions succeeded).
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success => Errors.Count == 0 && Solutions.Count > 0 && Solutions.All(s => s.Success);

    /// <summary>
    /// Results for each solution processed.
    /// </summary>
    [JsonPropertyName("solutions")]
    public List<SolutionResult> Solutions { get; init; } = new();

    /// <summary>
    /// Aggregated totals across all solutions.
    /// </summary>
    [JsonPropertyName("totals")]
    public BatchTotals Totals => new()
    {
        Solutions = Solutions.Count,
        Succeeded = Solutions.Count(s => s.Success),
        Failed = Solutions.Count(s => !s.Success),
        ProjectsProcessed = Solutions.Sum(s => s.Summary?.ProjectsProcessed ?? 0),
        PackagesFound = Solutions.Sum(s => s.Summary?.PackagesFound ?? 0),
        ConflictsResolved = Solutions.Sum(s => s.Summary?.ConflictsResolved ?? 0)
    };

    /// <summary>
    /// Errors that occurred at the batch level.
    /// </summary>
    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = new();

    /// <summary>
    /// Whether this was a dry-run.
    /// </summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; init; }

    /// <summary>
    /// Timestamp when the batch operation completed.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>
    /// Exit code for the batch operation.
    /// </summary>
    [JsonIgnore]
    public int ExitCode
    {
        get
        {
            if (Success)
            {
                return ExitCodes.Success;
            }
            if (Solutions.Any(s => s.ExitCode == ExitCodes.VersionConflict))
            {
                return ExitCodes.VersionConflict;
            }
            return ExitCodes.FileOperationError;
        }
    }
}

/// <summary>
/// Result for a single solution in a batch operation.
/// </summary>
public class SolutionResult
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("summary")]
    public OperationSummary? Summary { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("propsFile")]
    public string? PropsFile { get; init; }

    /// <summary>
    /// Baseline fingerprints this solution's findings actually matched. Internal batch-level
    /// staleness accounting only — never serialized, unlike <see cref="Summary"/>, whose stale
    /// count is a single-solution view.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<string>? MatchedBaselineFingerprints { get; init; }

    /// <summary>Rules this solution's run could not have judged; see <see cref="MatchedBaselineFingerprints"/>.</summary>
    [JsonIgnore]
    public IReadOnlyCollection<AnalysisIssueCode>? UnevaluatedRuleCodes { get; init; }
}

public partial class BatchResult
{
    /// <summary>
    /// Batch-wide count of baseline entries that matched no finding in any solution. Null when no
    /// baseline was used, it could not be read, or the batch stopped early — a partial run's union
    /// of matches would classify every skipped solution's live debt as fixed.
    /// </summary>
    [JsonPropertyName("baselineStaleEntries")]
    public int? BaselineStaleEntries { get; set; }

    /// <summary>
    /// Rule IDs the baselines cite that no current rule publishes, across the whole batch. Absent
    /// under the same conditions as <see cref="BaselineStaleEntries"/>.
    /// </summary>
    [JsonPropertyName("baselineUnknownRuleCodes")]
    public IReadOnlyList<string>? BaselineUnknownRuleCodes { get; set; }

    /// <summary>True when every discovered solution was evaluated, making a staleness verdict trustworthy.</summary>
    [JsonIgnore]
    public bool BaselineVerdictComplete { get; set; }
}

/// <summary>
/// Aggregated totals for a batch operation.
/// </summary>
public class BatchTotals
{
    [JsonPropertyName("solutions")]
    public int Solutions { get; init; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("projectsProcessed")]
    public int ProjectsProcessed { get; init; }

    [JsonPropertyName("packagesFound")]
    public int PackagesFound { get; init; }

    [JsonPropertyName("conflictsResolved")]
    public int ConflictsResolved { get; init; }
}
