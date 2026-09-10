using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--list-backups --output Json</c> promises CI scripts one parseable document carrying the
/// same backup history the console table renders — sets, files, sizes — plus
/// <c>directoryExists</c>, so a consumer can tell "no backups yet" apart from "no backup
/// directory" without parsing a warning.
/// </summary>
public class ListBackupsJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Parse(Serialize(directoryExists: true, Backups()));

        root.GetProperty("operation").GetString().Should().Be("list-backups");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("backupDir").GetString().Should().Be("/backups");
        root.GetProperty("directoryExists").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Serialize_MissingDirectory_CarriesTheVerdictAsData()
    {
        // The console path reports a missing directory as a warning and exits 0; the document
        // carries the same verdict as a field, so a consumer can tell "no backups yet" apart from
        // "no backup directory".
        var root = Parse(Serialize(directoryExists: false, []));

        root.GetProperty("directoryExists").GetBoolean().Should().BeFalse();
        root.GetProperty("backups").GetArrayLength().Should().Be(0);
        root.GetProperty("summary").GetProperty("sets").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Serialize_BackupSets_CarryTimestampFilesAndNames()
    {
        var root = Parse(Serialize(directoryExists: true, Backups()));
        var backup = root.GetProperty("backups")[0];

        backup.GetProperty("timestamp").GetString().Should().Be("20240101_000000");
        backup.GetProperty("files").GetInt32().Should().Be(2);
        backup.GetProperty("fileNames").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Serialize_SummaryFoldsTheTotals()
    {
        var root = Parse(Serialize(directoryExists: true, Backups()));
        var summary = root.GetProperty("summary");

        summary.GetProperty("sets").GetInt32().Should().Be(1);
        summary.GetProperty("files").GetInt32().Should().Be(2);
    }

    private static string Serialize(bool directoryExists, IReadOnlyList<BackupSetInfo> backups) =>
        ListBackupsJsonWriter.Serialize("/backups", directoryExists, backups, ExitCodes.Success);

    private static List<BackupSetInfo> Backups() =>
    [
        new BackupSetInfo
        {
            Timestamp = "20240101_000000",
            Files = ["/backups/App.csproj.backup_1", "/backups/Lib.csproj.backup_1"],
        },
    ];

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
