using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for ConfigService covering config discovery, JSON parsing, merging, and edge cases.
/// Hunting for bugs in directory traversal, JSON parsing with comments, and CLI option precedence.
/// </summary>
public class ConfigServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;
    private readonly ConfigService _configService;

    public ConfigServiceTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateConfigTest_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        _configService = new ConfigService(_console);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region LoadConfig Tests

    [Fact]
    public void LoadConfig_ConfigFileExists_ReturnsConfig()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        var configContent =
            @"{
  ""conflictStrategy"": ""Lowest"",
  ""backup"": false
}";
        File.WriteAllText(configPath, configContent);

        // Act
        var result = _configService.LoadConfig(_testDirectory);

        // Assert
        result.Should().NotBeNull();
        result!.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
        result.Backup.Should().BeFalse();
    }

    [Fact]
    public void LoadConfig_NoConfigFile_ReturnsNull()
    {
        // Arrange - empty directory with no config file

        // Act
        var result = _configService.LoadConfig(_testDirectory);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void LoadConfig_InvalidJson_ReturnsNull()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        File.WriteAllText(configPath, "{ invalid json without closing brace");

        // Act
        var result = _configService.LoadConfig(_testDirectory);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region DiscoverConfig Tests

    [Fact]
    public void DiscoverConfig_ConfigInCurrentDirectory_FindsConfig()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        File.WriteAllText(configPath, "{}");

        // Act
        var result = ConfigService.DiscoverConfig(_testDirectory);

        // Assert
        result.Should().Be(configPath);
    }

    [Fact]
    public void DiscoverConfig_ConfigInParentDirectory_FindsConfig()
    {
        // Arrange
        var parentDir = _testDirectory;
        var childDir = Path.Combine(parentDir, "subdir");
        Directory.CreateDirectory(childDir);

        var configPath = Path.Combine(parentDir, ".cpmigrate.json");
        File.WriteAllText(configPath, "{}");

        // Act
        var result = ConfigService.DiscoverConfig(childDir);

        // Assert
        result.Should().Be(configPath);
    }

    [Fact]
    public void DiscoverConfig_ConfigInGrandparentDirectory_FindsConfig()
    {
        // Arrange
        var grandparentDir = _testDirectory;
        var parentDir = Path.Combine(grandparentDir, "parent");
        var childDir = Path.Combine(parentDir, "child");
        Directory.CreateDirectory(childDir);

        var configPath = Path.Combine(grandparentDir, ".cpmigrate.json");
        File.WriteAllText(configPath, "{}");

        // Act
        var result = ConfigService.DiscoverConfig(childDir);

        // Assert
        result.Should().Be(configPath);
    }

    [Fact]
    public void DiscoverConfig_NoConfigFound_ReturnsNull()
    {
        // Arrange - empty directory hierarchy
        var childDir = Path.Combine(_testDirectory, "subdir");
        Directory.CreateDirectory(childDir);

        // Act
        var result = ConfigService.DiscoverConfig(childDir);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void DiscoverConfig_MultipleConfigFiles_ReturnsClosestToStart()
    {
        // Arrange - Config in both grandparent and parent
        var grandparentDir = _testDirectory;
        var parentDir = Path.Combine(grandparentDir, "parent");
        var childDir = Path.Combine(parentDir, "child");
        Directory.CreateDirectory(childDir);

        var grandparentConfigPath = Path.Combine(grandparentDir, ".cpmigrate.json");
        var parentConfigPath = Path.Combine(parentDir, ".cpmigrate.json");
        File.WriteAllText(grandparentConfigPath, "{}");
        File.WriteAllText(parentConfigPath, "{}");

        // Act
        var result = ConfigService.DiscoverConfig(childDir);

        // Assert - Should return the closest config (parent)
        result.Should().Be(parentConfigPath);
    }

    #endregion

    #region ParseConfig Tests

    [Fact]
    public void ParseConfig_AllProperties_ParsesCorrectly()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        var configContent =
            @"{
  ""conflictStrategy"": ""Lowest"",
  ""backup"": true,
  ""backupDir"": ""backup"",
  ""addGitignore"": true,
  ""keepVersionAttributes"": false,
  ""mergeExisting"": true,
  ""outputFormat"": ""Json"",
  ""retention"": {
    ""enabled"": true,
    ""maxBackups"": 10
  },
  ""excludeDirectories"": [""node_modules"", ""bin"", ""obj""]
}";
        File.WriteAllText(configPath, configContent);

        // Act
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().NotBeNull();
        result!.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
        result.Backup.Should().BeTrue();
        result.BackupDir.Should().Be("backup");
        result.AddGitignore.Should().BeTrue();
        result.KeepVersionAttributes.Should().BeFalse();
        result.MergeExisting.Should().BeTrue();
        result.OutputFormat.Should().Be(OutputFormat.Json);
        result.Retention.Should().NotBeNull();
        result.Retention!.Enabled.Should().BeTrue();
        result.Retention.MaxBackups.Should().Be(10);
        result.ExcludeDirectories.Should().HaveCount(3);
        result.ExcludeDirectories.Should().Contain("node_modules");
    }

    [Fact]
    public void ParseConfig_PartialProperties_ParsesSuccessfully()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        var configContent =
            @"{
  ""conflictStrategy"": ""Highest"",
  ""backup"": true
}";
        File.WriteAllText(configPath, configContent);

        // Act
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().NotBeNull();
        result!.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
        result.Backup.Should().BeTrue();
    }

    [Fact]
    public void ParseConfig_EmptyJson_ReturnsEmptyConfig()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        File.WriteAllText(configPath, "{}");

        // Act
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void ParseConfig_JsonWithComments_ParsesSuccessfully()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        var configContent =
            @"{
  // This is a comment
  ""conflictStrategy"": ""Lowest"",
  ""backup"": true // inline comment
}";
        File.WriteAllText(configPath, configContent);

        // Act
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().NotBeNull();
        result!.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
        result.Backup.Should().BeTrue();
    }

    [Fact]
    public void ParseConfig_JsonWithTrailingCommas_ParsesSuccessfully()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        var configContent =
            @"{
  ""conflictStrategy"": ""Highest"",
  ""backup"": true,
}";
        File.WriteAllText(configPath, configContent);

        // Act
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().NotBeNull();
        result!.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
        result.Backup.Should().BeTrue();
    }

    [Fact]
    public void ParseConfig_InvalidJson_ReturnsNull()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        File.WriteAllText(configPath, "{ not valid json");

        // Act
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseConfig_NonExistentFile_ReturnsNull()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.json");

        // Act
        var result = _configService.ParseConfig(nonExistentPath);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseConfig_CaseInsensitivePropertyNames_ParsesCorrectly()
    {
        // Arrange - lowercase property names
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");
        var configContent =
            @"{
  ""CONFLICTSTRATEGY"": ""Lowest"",
  ""BACKUP"": true,
  ""backupdir"": ""backup""
}";
        File.WriteAllText(configPath, configContent);

        // Act
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().NotBeNull();
        result!.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
        result.Backup.Should().BeTrue();
        result.BackupDir.Should().Be("backup");
    }

    #endregion

    #region MergeConfig Tests

    [Fact]
    public void MergeConfig_MergeExistingFromConfig_WhenCliNotProvided()
    {
        var options = new Options { MergeExisting = false };
        var config = new ConfigModel { MergeExisting = true };

        ConfigService.MergeConfig(options, config, new HashSet<string>());

        options.MergeExisting.Should().BeTrue();
    }

    [Fact]
    public void MergeConfig_MergeExistingDoesNotOverrideCliProvidedValue()
    {
        var options = new Options { MergeExisting = false };
        var config = new ConfigModel { MergeExisting = true };

        ConfigService.MergeConfig(
            options,
            config,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "merge" }
        );

        options.MergeExisting.Should().BeFalse();
    }

    [Fact]
    public void MergeConfig_ConflictStrategyFromConfig_WhenCliNotProvided()
    {
        // Arrange
        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };
        var config = new ConfigModel { ConflictStrategy = ConflictStrategy.Lowest };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert - Config should override default
        options.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
    }

    [Fact]
    public void MergeConfig_ConflictStrategyDoesNotOverrideCliProvidedValue()
    {
        // Arrange
        var options = new Options { ConflictStrategy = ConflictStrategy.Highest };
        var config = new ConfigModel { ConflictStrategy = ConflictStrategy.Lowest };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string> { "conflict-strategy" });

        // Assert - CLI should win
        options.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
    }

    [Fact]
    public void MergeConfig_BackupFromConfig_InvertsToNoBackup()
    {
        // Arrange - Config has backup: false, so NoBackup should become true
        var options = new Options { NoBackup = false };
        var config = new ConfigModel { Backup = false };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.NoBackup.Should().BeTrue();
    }

    [Fact]
    public void MergeConfig_BackupDirFromConfig_WhenCliNotProvided()
    {
        // Arrange
        var options = new Options { BackupDir = string.Empty };
        var config = new ConfigModel { BackupDir = "config-backup" };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.BackupDir.Should().Be("config-backup");
    }

    [Fact]
    public void MergeConfig_AddGitignoreFromConfig_WhenCliNotProvided()
    {
        // Arrange
        var options = new Options { AddBackupToGitignore = false };
        var config = new ConfigModel { AddGitignore = true };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.AddBackupToGitignore.Should().BeTrue();
    }

    [Fact]
    public void MergeConfig_KeepAttributesFromConfig_WhenCliNotProvided()
    {
        // Arrange
        var options = new Options { KeepAttributes = false };
        var config = new ConfigModel { KeepVersionAttributes = true };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.KeepAttributes.Should().BeTrue();
    }

    [Fact]
    public void MergeConfig_OutputFormatFromConfig_WhenCliNotProvided()
    {
        // Arrange
        var options = new Options { Output = OutputFormat.Terminal };
        var config = new ConfigModel { OutputFormat = OutputFormat.Json };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.Output.Should().Be(OutputFormat.Json);
    }

    [Fact]
    public void MergeConfig_RetentionFromConfig_WhenCliNotProvided()
    {
        // Arrange
        var options = new Options { Retention = 0 };
        var config = new ConfigModel
        {
            Retention = new RetentionConfig { Enabled = true, MaxBackups = 10 },
        };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.Retention.Should().Be(10);
    }

    [Fact]
    public void MergeConfig_RetentionDisabled_DoesNotSetRetention()
    {
        // Arrange
        var options = new Options { Retention = 0 };
        var config = new ConfigModel
        {
            Retention = new RetentionConfig { Enabled = false, MaxBackups = 10 },
        };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.Retention.Should().Be(0);
    }

    [Fact]
    public void MergeConfig_NullCliArgsProvided_TreatsAsEmpty()
    {
        // Arrange
        var options = new Options { MergeExisting = false };
        var config = new ConfigModel { MergeExisting = true };

        // Act - Pass null for cliArgsProvided
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.MergeExisting.Should().BeTrue();
    }

    [Fact]
    public void MergeConfig_AllPropertiesFromConfig_WhenNoCliArgsProvided()
    {
        // Arrange
        var options = new Options();
        var config = new ConfigModel
        {
            ConflictStrategy = ConflictStrategy.Lowest,
            Backup = false,
            BackupDir = "config-backup",
            AddGitignore = true,
            KeepVersionAttributes = true,
            MergeExisting = true,
            OutputFormat = OutputFormat.Json,
            Retention = new RetentionConfig { Enabled = true, MaxBackups = 42 },
        };

        // Act
        ConfigService.MergeConfig(options, config, new HashSet<string>());

        // Assert
        options.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
        options.NoBackup.Should().BeTrue();
        options.BackupDir.Should().Be("config-backup");
        options.AddBackupToGitignore.Should().BeTrue();
        options.KeepAttributes.Should().BeTrue();
        options.MergeExisting.Should().BeTrue();
        options.Output.Should().Be(OutputFormat.Json);
        options.Retention.Should().Be(42);
    }

    [Fact]
    public void MergeConfig_AllCliArgsProvided_BlockAllConfigValues()
    {
        // Arrange
        var options = new Options();
        var config = new ConfigModel
        {
            ConflictStrategy = ConflictStrategy.Lowest,
            Backup = false,
            BackupDir = "config-backup",
            AddGitignore = true,
            KeepVersionAttributes = true,
            MergeExisting = true,
            OutputFormat = OutputFormat.Json,
            Retention = new RetentionConfig { Enabled = true, MaxBackups = 42 },
        };
        var cliArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "conflict-strategy",
            "no-backup",
            "backup-dir",
            "add-gitignore",
            "keep-attrs",
            "merge",
            "output",
            "retention",
        };

        // Act
        ConfigService.MergeConfig(options, config, cliArgs);

        // Assert
        options.ConflictStrategy.Should().Be(ConflictStrategy.Highest); // default
        options.NoBackup.Should().BeFalse(); // default
        options.BackupDir.Should().Be("."); // default
        options.AddBackupToGitignore.Should().BeFalse(); // default
        options.KeepAttributes.Should().BeFalse(); // default
        options.MergeExisting.Should().BeFalse(); // default
        options.Output.Should().Be(OutputFormat.Terminal); // default
        options.Retention.Should().Be(0); // default
    }

    #endregion

    #region CreateSampleConfig Tests

    [Fact]
    public void CreateSampleConfig_CreatesValidConfigFile()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");

        // Act
        _configService.CreateSampleConfig(configPath);

        // Assert
        File.Exists(configPath).Should().BeTrue();
        var content = File.ReadAllText(configPath);
        content.Should().Contain("conflictStrategy");
        content.Should().Contain("backup");
        content.Should().Contain("retention");
    }

    [Fact]
    public void CreateSampleConfig_CreatedFileCanBeParsed()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, ".cpmigrate.json");

        // Act
        _configService.CreateSampleConfig(configPath);
        var result = _configService.ParseConfig(configPath);

        // Assert
        result.Should().NotBeNull();
        result!.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
        result.Backup.Should().BeTrue();
        result.BackupDir.Should().Be(".cpmigrate_backup");
        result.Retention.Should().NotBeNull();
        result.Retention!.Enabled.Should().BeTrue();
        result.Retention.MaxBackups.Should().Be(5);
    }

    #endregion
}
