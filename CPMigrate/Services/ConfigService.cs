using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Service for discovering and loading .cpmigrate.json configuration files.
/// </summary>
public class ConfigService
{
    private const string ConfigFileName = ".cpmigrate.json";

    private readonly IConsoleService? _consoleService;

    /// <summary>
    /// Creates a new ConfigService instance.
    /// </summary>
    /// <param name="consoleService">Optional console service for logging.</param>
    public ConfigService(IConsoleService? consoleService = null)
    {
        _consoleService = consoleService;
    }

    /// <summary>
    /// Discovers and loads a config file from the specified directory or its parents.
    /// </summary>
    /// <param name="startDirectory">Directory to start searching from.</param>
    /// <returns>The loaded config, or null if no config file found.</returns>
    public ConfigModel? LoadConfig(string startDirectory)
    {
        var (config, _, errorMessage) = LoadConfigDetailed(startDirectory);
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _consoleService?.Warning(errorMessage);
        }

        return config;
    }

    public (ConfigModel? Config, string? ConfigPath, string? ErrorMessage) LoadConfigDetailed(
        string startDirectory
    )
    {
        var configPath = DiscoverConfig(startDirectory);
        if (configPath == null)
        {
            return (null, null, null);
        }

        return ParseConfigDetailed(configPath);
    }

    /// <summary>
    /// Discovers a .cpmigrate.json file starting from the specified directory.
    /// Searches the directory and its parents up to the filesystem root.
    /// </summary>
    /// <param name="startDirectory">Directory to start searching from.</param>
    /// <returns>Path to the config file, or null if not found.</returns>
    public static string? DiscoverConfig(string startDirectory)
    {
        var directory = Path.GetFullPath(startDirectory);

        while (!string.IsNullOrEmpty(directory))
        {
            var configPath = Path.Combine(directory, ConfigFileName);
            if (File.Exists(configPath))
            {
                return configPath;
            }

            var parent = Directory.GetParent(directory);
            if (parent == null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return null;
    }

    /// <summary>
    /// Parses a config file from the specified path.
    /// </summary>
    /// <param name="configPath">Path to the config file.</param>
    /// <returns>The parsed config, or null if parsing failed.</returns>
    public ConfigModel? ParseConfig(string configPath)
    {
        var (config, _, errorMessage) = ParseConfigDetailed(configPath);
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _consoleService?.Warning(errorMessage);
        }

        return config;
    }

    /// <summary>
    /// Enum-valued settings are written and read as names, not numbers. The published schema
    /// (<c>schemas/cpmigrate.schema.json</c>) permits only names and the documentation shows names,
    /// so without this converter every documented enum setting failed to parse — the config was
    /// reported as invalid and silently fell back to defaults.
    /// </summary>
    /// <summary>
    /// Names only. Integers are rejected because the schema permits only names, and an out-of-range
    /// number would cast to an undefined severity that no real finding could reach — silently
    /// disabling the CI gate instead of reporting a bad config.
    /// </summary>
    private static readonly JsonStringEnumConverter EnumConverter = new(
        namingPolicy: null,
        allowIntegerValues: false
    );

    private readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { EnumConverter },
    };

    private readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { EnumConverter },
    };

    private (ConfigModel? Config, string? ConfigPath, string? ErrorMessage) ParseConfigDetailed(
        string configPath
    )
    {
        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<ConfigModel>(json, _readOptions);

            if (config is null)
            {
                return (
                    null,
                    configPath,
                    $"Config file {configPath} deserialized to null — check the JSON structure."
                );
            }

            var validationWarning = ValidateConfig(config);
            return (config, configPath, validationWarning);
        }
        catch (JsonException ex)
        {
            var hint = ex.LineNumber.HasValue
                ? $" (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1})"
                : string.Empty;
            return (null, configPath, $"Invalid JSON in {configPath}{hint}: {ex.Message}");
        }
        catch (IOException ex)
        {
            return (null, configPath, $"Failed to read config file {configPath}: {ex.Message}");
        }
    }

    private static string? ValidateConfig(ConfigModel config)
    {
        var warnings = new List<string>();

        if (config.Retention is { Enabled: true, MaxBackups: <= 0 })
        {
            warnings.Add("retention.maxBackups is 0 or negative — no backups will be kept.");
        }

        if (config.Backup == false && config.AddGitignore == true)
        {
            warnings.Add(
                "addGitignore is true but backup is false — no backup directory will be created to ignore."
            );
        }

        if (!string.IsNullOrWhiteSpace(config.Baseline) && config.FailOn == FailOnSeverity.Never)
        {
            warnings.Add(
                "baseline is set but failOn is Never — the baseline will never gate anything."
            );
        }

        return warnings.Count > 0 ? string.Join(" ", warnings) : null;
    }

    /// <summary>
    /// Merges config file settings into Options.
    /// CLI options take precedence over config file values.
    /// </summary>
    /// <param name="options">The CLI options to merge into.</param>
    /// <param name="config">The config file settings.</param>
    /// <param name="cliArgsProvided">Set of CLI argument names that were explicitly provided.</param>
    public static void MergeConfig(
        Options options,
        ConfigModel config,
        HashSet<string>? cliArgsProvided = null
    )
    {
        cliArgsProvided ??= new HashSet<string>();

        foreach (var rule in MergeRules)
        {
            rule.TryApply(options, config, cliArgsProvided);
        }
    }

    private static readonly List<ConfigMergeRule> MergeRules = new()
    {
        new(
            c => c.ConflictStrategy.HasValue,
            "conflict-strategy",
            (o, c) => o.ConflictStrategy = c.ConflictStrategy.GetValueOrDefault()
        ),
        new(
            c => c.Backup.HasValue,
            "no-backup",
            (o, c) => o.NoBackup = !c.Backup.GetValueOrDefault()
        ),
        new(
            c => !string.IsNullOrEmpty(c.BackupDir),
            "backup-dir",
            (o, c) => o.BackupDir = c.BackupDir ?? string.Empty
        ),
        new(
            c => c.AddGitignore.HasValue,
            "add-gitignore",
            (o, c) => o.AddBackupToGitignore = c.AddGitignore.GetValueOrDefault()
        ),
        new(
            c => c.KeepVersionAttributes.HasValue,
            "keep-attrs",
            (o, c) => o.KeepAttributes = c.KeepVersionAttributes.GetValueOrDefault()
        ),
        new(
            c => c.MergeExisting.HasValue,
            "merge",
            (o, c) => o.MergeExisting = c.MergeExisting.GetValueOrDefault()
        ),
        new(c => !string.IsNullOrEmpty(c.Baseline), "baseline", (o, c) => o.Baseline = c.Baseline),
        new(c => c.FailOn.HasValue, "fail-on", (o, c) => o.FailOn = c.FailOn.GetValueOrDefault()),
        // Flattened to the same Rule=Value spec the CLI takes, so the config file and the flag
        // cannot drift into two parsers that disagree about what a policy means.
        new(
            c => c.Rules is { Count: > 0 },
            "rules",
            (o, c) =>
                o.Rules = string.Join(",", c.Rules!.Select(entry => $"{entry.Key}={entry.Value}"))
        ),
        new(
            c => c.OutputFormat.HasValue,
            "output",
            (o, c) => o.Output = c.OutputFormat.GetValueOrDefault()
        ),
        new(
            c => c.Retention is { Enabled: true },
            "retention",
            (o, c) => o.Retention = c.Retention?.MaxBackups ?? 0
        ),
    };

    private sealed record ConfigMergeRule(
        Func<ConfigModel, bool> HasValue,
        string CliArg,
        Action<Options, ConfigModel> Apply
    )
    {
        public void TryApply(Options options, ConfigModel config, HashSet<string> cliArgsProvided)
        {
            if (HasValue(config) && !cliArgsProvided.Contains(CliArg))
            {
                Apply(options, config);
            }
        }
    }

    /// <summary>
    /// Creates a sample config file at the specified path.
    /// </summary>
    /// <param name="path">Path to create the config file.</param>
    public void CreateSampleConfig(string path)
    {
        var sampleConfig = new ConfigModel
        {
            Schema =
                "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/schemas/cpmigrate.schema.json",
            ConflictStrategy = CPMigrate.ConflictStrategy.Highest,
            Backup = true,
            BackupDir = ".cpmigrate_backup",
            AddGitignore = true,
            KeepVersionAttributes = false,
            MergeExisting = false,
            OutputFormat = OutputFormat.Terminal,
            Retention = new RetentionConfig { Enabled = true, MaxBackups = 5 },
            ExcludeDirectories = new List<string>
            {
                "node_modules",
                "bin",
                "obj",
                ".git",
                "packages",
            },
        };

        var json = JsonSerializer.Serialize(sampleConfig, _writeOptions);
        File.WriteAllText(path, json);
    }
}
