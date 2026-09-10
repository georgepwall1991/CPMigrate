using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--init --output Json</c> promises CI scripts one parseable document carrying the same
/// verdict the console prints — whether the config file was written, and where — so a consumer
/// can tell "wrote it" apart from "wrote nothing because it was already there".
/// </summary>
public class InitJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Parse(Serialize("created", forced: false, ExitCodes.Success));

        root.GetProperty("operation").GetString().Should().Be("init");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("configPath").GetString().Should().Be("/repo/.cpmigrate.json");
        root.GetProperty("status").GetString().Should().Be("created");
        root.GetProperty("forced").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Serialize_Exists_CarriesTheRefusalAsData()
    {
        // The console path reports an existing file as a warning and exits 2; the document
        // carries the same verdict as a field, so a consumer can tell "wrote nothing" apart from
        // "wrote it".
        var root = Parse(Serialize("exists", forced: false, ExitCodes.FileOperationError));

        root.GetProperty("status").GetString().Should().Be("exists");
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.FileOperationError);
    }

    [Fact]
    public void Serialize_Overwritten_CarriesForced()
    {
        var root = Parse(Serialize("overwritten", forced: true, ExitCodes.Success));

        root.GetProperty("status").GetString().Should().Be("overwritten");
        root.GetProperty("forced").GetBoolean().Should().BeTrue();
    }

    private static string Serialize(string status, bool forced, int exitCode) =>
        InitJsonWriter.Serialize("/repo/.cpmigrate.json", status, forced, exitCode);

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
