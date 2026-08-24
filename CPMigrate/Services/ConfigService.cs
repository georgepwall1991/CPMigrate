using System.Reflection;
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

            var warnings = new[] { ValidateConfig(config), DetectUnknownKeys(json, configPath) }
                .Where(warning => warning is not null)
                .Select(warning => warning!)
                .ToList();
            return (config, configPath, warnings.Count > 0 ? string.Join(" ", warnings) : null);
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
    /// System.Text.Json ignores properties it cannot map, so a typo such as <c>fialOn</c> silently
    /// disables the team policy it was meant to set. The JSON is therefore re-walked and every key
    /// that matches no known model property — case-insensitively, since casing already deserializes
    /// fine — is reported as a warning. The known keys come from the model's own
    /// <see cref="JsonPropertyNameAttribute"/> values rather than a copied list, so they cannot
    /// drift from what actually deserializes.
    /// </summary>
    private static readonly IReadOnlyDictionary<
        Type,
        IReadOnlyDictionary<string, PropertyInfo>
    > KnownKeysByType = BuildKnownKeys(typeof(ConfigModel));

    private static Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> BuildKnownKeys(
        Type root
    )
    {
        var knownKeys = new Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>>();
        var pending = new Queue<Type>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!knownKeys.TryAdd(type, new Dictionary<string, PropertyInfo>()))
            {
                continue;
            }

            var byName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (name == null)
                {
                    continue;
                }

                byName.Add(name, property);

                var nested = Nullable.GetUnderlyingType(property.PropertyType)
                    ?? property.PropertyType;
                if (
                    nested.IsClass
                    && !typeof(System.Collections.IEnumerable).IsAssignableFrom(nested)
                )
                {
                    pending.Enqueue(nested);
                }
            }

            knownKeys[type] = byName;
        }

        return knownKeys;
    }

    private static string? DetectUnknownKeys(string json, string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }
            );

            var unknowns = new List<(string Key, string? Suggestion)>();
            CollectUnknownKeys(document.RootElement, typeof(ConfigModel), string.Empty, unknowns);
            if (unknowns.Count == 0)
            {
                return null;
            }

            var described = unknowns.Select(unknown =>
                unknown.Suggestion is null
                    ? $"'{unknown.Key}'"
                    : $"'{unknown.Key}' — did you mean '{unknown.Suggestion}'?"
            );
            return $"Unknown setting(s) in {configPath}: {string.Join(", ", described)}.";
        }
        catch (JsonException)
        {
            // Malformed JSON was already rejected above with line information; this lint must never
            // turn a loadable config into a failed one.
            return null;
        }
    }

    private static void CollectUnknownKeys(
        JsonElement element,
        Type type,
        string prefix,
        List<(string Key, string? Suggestion)> unknowns
    )
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var known = KnownKeysByType[type];
        foreach (var property in element.EnumerateObject())
        {
            if (!known.TryGetValue(property.Name, out var match))
            {
                unknowns.Add(
                    (
                        $"{prefix}{property.Name}",
                        NearestKnownKey(property.Name, prefix, known.Keys)
                    )
                );
                continue;
            }

            var nested =
                Nullable.GetUnderlyingType(match.PropertyType) ?? match.PropertyType;
            if (
                KnownKeysByType.ContainsKey(nested)
                && property.Value.ValueKind == JsonValueKind.Object
            )
            {
                CollectUnknownKeys(
                    property.Value,
                    nested,
                    $"{prefix}{property.Name}.",
                    unknowns
                );
            }
        }
    }

    /// <summary>
    /// Finds the known sibling key closest to an unknown one: within a small edit distance, or
    /// contained in it (or containing it), so <c>fialOn</c> suggests <c>failOn</c>. Returns null
    /// when nothing plausibly matches — guessing would be worse than silence.
    /// </summary>
    private static string? NearestKnownKey(
        string name,
        string prefix,
        IEnumerable<string> candidates
    )
    {
        const int maxEditDistance = 3;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = EditDistance(name, candidate);
            var contained =
                name.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.Contains(name, StringComparison.OrdinalIgnoreCase);
            if (distance > maxEditDistance && !contained)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = $"{prefix}{candidate}";
            }
        }

        return best;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution
                );
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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
        new(c => c.Verify.HasValue, "verify", (o, c) => o.Verify = c.Verify.GetValueOrDefault()),
        new(
            c => c.VerifyStrict.HasValue,
            "verify-strict",
            (o, c) => o.VerifyStrict = c.VerifyStrict.GetValueOrDefault()
        ),
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
