using System.Text.Json.Serialization;

namespace CPMigrate.Models;

/// <summary>
/// Result of a batch operation across multiple solutions.
/// </summary>
public class BatchResult
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
    public string Operation { get; init; } = "batch-migrate";

    /// <summary>
    /// Overall success status (true only if all solutions succeeded).
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success => Solutions.All(s => s.Success);

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
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");

    /// <summary>
    /// Exit code for the batch operation.
    /// </summary>
    [JsonIgnore]
    public int ExitCode => Success ? ExitCodes.Success :
        Solutions.Any(s => s.ExitCode == ExitCodes.VersionConflict) ? ExitCodes.VersionConflict :
        ExitCodes.FileOperationError;
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

    [JsonPropertyName("conflicts")]
    public List<ConflictInfo>? Conflicts { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("propsFile")]
    public string? PropsFile { get; init; }
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
