using System.Text.Json.Serialization;
using CPMigrate.Services;

namespace CPMigrate.Models;

/// <summary>
/// Configuration file model for .cpmigrate.json files.
/// </summary>
public class ConfigModel
{
    /// <summary>
    /// JSON schema reference for IDE autocomplete.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// How to handle version conflicts: Highest (default), Lowest, or Fail.
    /// </summary>
    [JsonPropertyName("conflictStrategy")]
    public ConflictStrategy? ConflictStrategy { get; set; }

    /// <summary>
    /// Whether to create backups before modifying files.
    /// </summary>
    [JsonPropertyName("backup")]
    public bool? Backup { get; set; }

    /// <summary>
    /// Directory for backup files.
    /// </summary>
    [JsonPropertyName("backupDir")]
    public string? BackupDir { get; set; }

    /// <summary>
    /// Whether to add backup directory to .gitignore.
    /// </summary>
    [JsonPropertyName("addGitignore")]
    public bool? AddGitignore { get; set; }

    /// <summary>
    /// Whether to keep Version attributes in project files.
    /// </summary>
    [JsonPropertyName("keepVersionAttributes")]
    public bool? KeepVersionAttributes { get; set; }

    /// <summary>
    /// Whether to merge into an existing Directory.Packages.props file.
    /// </summary>
    [JsonPropertyName("mergeExisting")]
    public bool? MergeExisting { get; set; }

    /// <summary>
    /// Output format: terminal or json.
    /// </summary>
    /// <summary>
    /// Lowest finding severity that fails the build. Belongs in the config file because it is a
    /// team-wide policy decision, not a per-invocation one.
    /// </summary>
    [JsonPropertyName("failOn")]
    public FailOnSeverity? FailOn { get; set; }

    /// <summary>
    /// Path to the accepted-findings baseline. A team-wide setting: the file is committed alongside
    /// the code it describes, so every run should find it without being told where it is.
    /// </summary>
    [JsonPropertyName("baseline")]
    public string? Baseline { get; set; }

    [JsonPropertyName("outputFormat")]
    public OutputFormat? OutputFormat { get; set; }

    /// <summary>
    /// Backup retention settings.
    /// </summary>
    [JsonPropertyName("retention")]
    public RetentionConfig? Retention { get; set; }

    /// <summary>
    /// Directories to exclude when scanning for solutions (batch mode).
    /// </summary>
    [JsonPropertyName("excludeDirectories")]
    public List<string>? ExcludeDirectories { get; set; }

    /// <summary>
    /// Per-rule policy: rule ID to either a severity or <c>none</c> to switch the rule off. Belongs
    /// in the config file because which rules a codebase cares about is a team decision that should
    /// hold for every run, not something each caller re-states.
    /// </summary>
    [JsonPropertyName("rules")]
    public Dictionary<string, string>? Rules { get; set; }
}

/// <summary>
/// Backup retention configuration.
/// </summary>
public class RetentionConfig
{
    /// <summary>
    /// Whether automatic retention is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of backups to keep.
    /// </summary>
    [JsonPropertyName("maxBackups")]
    public int MaxBackups { get; set; } = 5;
}
