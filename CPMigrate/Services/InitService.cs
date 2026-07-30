using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

internal sealed class InitService
{
    private const string ConfigFileName = ".cpmigrate.json";
#pragma warning disable S1075 // URIs should not be hardcoded - the published schema lives at a fixed location
    private const string SchemaUrl =
        "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/schemas/cpmigrate.schema.json";
#pragma warning restore S1075

    private readonly IConsoleService _console;

    public InitService(IConsoleService console)
    {
        _console = console;
    }

    public Task<int> RunAsync(string directory, bool force)
    {
        var targetDir = Directory.Exists(directory) ? directory : Path.GetDirectoryName(Path.GetFullPath(directory)) ?? ".";
        var configPath = Path.Combine(targetDir, ConfigFileName);

        _console.WriteHeader();
        _console.Banner("INITIALIZE CONFIG");
        _console.WriteLine();

        if (File.Exists(configPath) && !force)
        {
            _console.Warning($"A {ConfigFileName} already exists at: {configPath}");
            _console.Info("Run with --force to overwrite it.");
            return Task.FromResult(ExitCodes.FileOperationError);
        }

        var config = _console.IsInteractive
            ? BuildConfigInteractively()
            : BuildDefaultConfig();

        WriteConfig(configPath, config);

        _console.WriteLine();
        _console.Success($"Created {configPath}");
        _console.Dim("Commit this file so your team shares the same defaults.");
        _console.Dim("CLI flags always override config values.");

        return Task.FromResult(ExitCodes.Success);
    }

    private ConfigModel BuildDefaultConfig()
    {
        return new ConfigModel
        {
            Schema = SchemaUrl,
            ConflictStrategy = ConflictStrategy.Highest,
            Backup = true,
            AddGitignore = true,
            MergeExisting = false,
            OutputFormat = OutputFormat.Terminal,
            FailOn = FailOnSeverity.High,
            Baseline = Options.BaselineDefaultFileName,
            Retention = new RetentionConfig { Enabled = true, MaxBackups = 5 },
        };
    }

    private ConfigModel BuildConfigInteractively()
    {
        _console.Highlight("Answer a few questions to configure your team defaults.");
        _console.Dim("Press Enter to accept the default shown in brackets.");
        _console.WriteLine();

        var strategy = AskChoice(
            "Version conflict strategy",
            new[] { "Highest", "Lowest", "Fail" },
            0);

        var backup = _console.AskConfirmation("Create backups before modifying files?");
        var addGitignore = backup && _console.AskConfirmation("Add backup directory to .gitignore?");

        var failOn = AskChoice(
            "Lowest severity that fails CI (fail-on)",
            new[] { "Info", "Low", "Moderate", "High", "Critical", "Never" },
            3);

        var useBaseline = _console.AskConfirmation("Use a baseline file to gate only on new findings?");

        var retentionEnabled = _console.AskConfirmation("Enable automatic backup retention?");
        var maxBackups = retentionEnabled ? _console.AskInt("Maximum backups to keep", 5) : 5;

        return new ConfigModel
        {
            Schema = SchemaUrl,
            ConflictStrategy = Enum.Parse<ConflictStrategy>(strategy),
            Backup = backup,
            AddGitignore = addGitignore,
            MergeExisting = false,
            OutputFormat = OutputFormat.Terminal,
            FailOn = Enum.Parse<FailOnSeverity>(failOn),
            Baseline = useBaseline ? Options.BaselineDefaultFileName : null,
            Retention = new RetentionConfig { Enabled = retentionEnabled, MaxBackups = maxBackups },
        };
    }

    private string AskChoice(string prompt, string[] choices, int defaultIndex)
    {
        if (!_console.IsInteractive)
        {
            return choices[defaultIndex];
        }

        var selection = _console.AskSelection(prompt, choices);
        return selection;
    }

    private static void WriteConfig(string path, ConfigModel config)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(path, json + Environment.NewLine);
    }
}
