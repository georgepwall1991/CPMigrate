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
    /// Output format: terminal or json.
    /// </summary>
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
