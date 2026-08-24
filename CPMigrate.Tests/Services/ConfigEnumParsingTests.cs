using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// The published schema declares every enum-valued config property as a string, the README shows
/// strings, and CPMigrate's own sample config writes them — but nothing verified that a string
/// actually deserializes. It did not: the whole enum-valued config surface silently failed with a
/// parse warning and fell back to defaults.
/// </summary>
public class ConfigEnumParsingTests : IDisposable
{
    private readonly string _root;

    public ConfigEnumParsingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateConfigEnum_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LoadConfigDetailed_StringEnumValues_ParseWithoutError()
    {
        WriteConfig(
            """
            {
              "conflictStrategy": "Lowest",
              "outputFormat": "Json",
              "failOn": "High"
            }
            """
        );

        var (config, _, error, _) = new ConfigService(
            SilentConsoleService.Instance
        ).LoadConfigDetailed(_root);

        error.Should().BeNull();
        config.Should().NotBeNull();
        config!.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
        config.OutputFormat.Should().Be(OutputFormat.Json);
        config.FailOn.Should().Be(FailOnSeverity.High);
    }

    [Fact]
    public void LoadConfigDetailed_SarifOutputFormat_Parses()
    {
        WriteConfig("""{ "outputFormat": "Sarif" }""");

        var (config, _, error, _) = new ConfigService(
            SilentConsoleService.Instance
        ).LoadConfigDetailed(_root);

        error.Should().BeNull();
        config!.OutputFormat.Should().Be(OutputFormat.Sarif);
    }

    [Theory]
    [InlineData("""{ "failOn": 99 }""")]
    [InlineData("""{ "failOn": 3 }""")]
    [InlineData("""{ "conflictStrategy": 1 }""")]
    public void LoadConfigDetailed_NumericEnumValue_IsRejected(string json)
    {
        // The schema permits only names. An out-of-range number would cast to an undefined severity
        // that no real finding can reach, silently disabling the gate rather than reporting a bad
        // config — so even in-range numbers are refused, to keep one accepted spelling.
        WriteConfig(json);

        var (config, _, error, _) = new ConfigService(
            SilentConsoleService.Instance
        ).LoadConfigDetailed(_root);

        config.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LoadConfigDetailed_UnknownEnumValue_ReportsAParseError()
    {
        // A typo must be reported rather than silently ignored, or a team believes a gate is active
        // when it is not.
        WriteConfig("""{ "failOn": "Hgih" }""");

        var (config, _, error, _) = new ConfigService(
            SilentConsoleService.Instance
        ).LoadConfigDetailed(_root);

        config.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateSampleConfig_WritesAConfigThatCPMigrateCanReadBack()
    {
        // The generated sample previously serialized enums as numbers, which the published schema
        // rejects — so the file CPMigrate itself produced showed as invalid in an editor.
        var service = new ConfigService(SilentConsoleService.Instance);
        var path = Path.Combine(_root, ".cpmigrate.json");

        service.CreateSampleConfig(path);

        var contents = File.ReadAllText(path);
        contents
            .Should()
            .Contain("\"conflictStrategy\": \"Highest\"", "the schema permits only names");

        var (config, _, error, _) = service.LoadConfigDetailed(_root);
        error.Should().BeNull();
        config!.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
    }

    [Fact]
    public void MergeConfig_AppliesTheFailOnPolicyWhenNoCliFlagOverridesIt()
    {
        WriteConfig("""{ "failOn": "Critical" }""");
        var options = new Options { Analyze = true };

        var (config, _, _, _) = new ConfigService(SilentConsoleService.Instance).LoadConfigDetailed(_root);
        ConfigService.MergeConfig(options, config!, new HashSet<string>());

        options.FailOn.Should().Be(FailOnSeverity.Critical);
    }

    [Fact]
    public void MergeConfig_CliFlagWinsOverTheConfiguredPolicy()
    {
        WriteConfig("""{ "failOn": "Critical" }""");
        var options = new Options { Analyze = true, FailOn = FailOnSeverity.Low };

        var (config, _, _, _) = new ConfigService(SilentConsoleService.Instance).LoadConfigDetailed(_root);
        ConfigService.MergeConfig(options, config!, new HashSet<string> { "fail-on" });

        options.FailOn.Should().Be(FailOnSeverity.Low);
    }

    private void WriteConfig(string json)
    {
        File.WriteAllText(Path.Combine(_root, ".cpmigrate.json"), json);
    }
}
