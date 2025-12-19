using System.Text.Json;
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
        var configPath = DiscoverConfig(startDirectory);
        if (configPath == null)
        {
            return null;
        }

        return ParseConfig(configPath);
    }

    /// <summary>
    /// Discovers a .cpmigrate.json file starting from the specified directory.
    /// Searches the directory and its parents up to the filesystem root.
    /// </summary>
    /// <param name="startDirectory">Directory to start searching from.</param>
    /// <returns>Path to the config file, or null if not found.</returns>
    public string? DiscoverConfig(string startDirectory)
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
        try
        {
            var json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var config = JsonSerializer.Deserialize<ConfigModel>(json, options);
            _consoleService?.Dim($"Loaded config from: {configPath}");
            return config;
        }
        catch (JsonException ex)
        {
            _consoleService?.Warning($"Failed to parse config file {configPath}: {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            _consoleService?.Warning($"Failed to read config file {configPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Merges config file settings into Options.
    /// CLI options take precedence over config file values.
    /// </summary>
    /// <param name="options">The CLI options to merge into.</param>
    /// <param name="config">The config file settings.</param>
    /// <param name="cliArgsProvided">Set of CLI argument names that were explicitly provided.</param>
    public void MergeConfig(Options options, ConfigModel config, HashSet<string>? cliArgsProvided = null)
    {
        cliArgsProvided ??= new HashSet<string>();

        // Only apply config values if the CLI option wasn't explicitly provided

        if (config.ConflictStrategy.HasValue && !cliArgsProvided.Contains("conflict-strategy"))
        {
            options.ConflictStrategy = config.ConflictStrategy.Value;
        }

        if (config.Backup.HasValue && !cliArgsProvided.Contains("no-backup"))
        {
            options.NoBackup = !config.Backup.Value;
        }

        if (!string.IsNullOrEmpty(config.BackupDir) && !cliArgsProvided.Contains("backup-dir"))
        {
            options.BackupDir = config.BackupDir;
        }

        if (config.AddGitignore.HasValue && !cliArgsProvided.Contains("add-gitignore"))
        {
            options.AddBackupToGitignore = config.AddGitignore.Value;
        }

        if (config.KeepVersionAttributes.HasValue && !cliArgsProvided.Contains("keep-attrs"))
        {
            options.KeepAttributes = config.KeepVersionAttributes.Value;
        }

        if (config.MergeExisting.HasValue && !cliArgsProvided.Contains("merge"))
        {
            options.MergeExisting = config.MergeExisting.Value;
        }

        if (config.OutputFormat.HasValue && !cliArgsProvided.Contains("output"))
        {
            options.Output = config.OutputFormat.Value;
        }

        if (config.Retention != null)
        {
            if (config.Retention.Enabled && !cliArgsProvided.Contains("retention"))
            {
                options.Retention = config.Retention.MaxBackups;
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
            Schema = "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/schemas/cpmigrate.schema.json",
            ConflictStrategy = CPMigrate.ConflictStrategy.Highest,
            Backup = true,
            BackupDir = ".cpmigrate_backup",
            AddGitignore = true,
            KeepVersionAttributes = false,
            MergeExisting = false,
            OutputFormat = OutputFormat.Terminal,
            Retention = new RetentionConfig
            {
                Enabled = true,
                MaxBackups = 5
            },
            ExcludeDirectories = new List<string>
            {
                "node_modules",
                "bin",
                "obj",
                ".git",
                "packages"
            }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(sampleConfig, options);
        File.WriteAllText(path, json);
    }
}
