using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--prune-backups --output Json</c> and <c>--prune-all-backups --output Json</c> promise CI
/// scripts one parseable document carrying the same verdict the console prints — the outcome as
/// a status token, the totals below the banner, and every deletion error.
/// </summary>
public class PruneBackupsJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Parse(Serialize("prune-backups", "pruned", found: 3, Result(removed: 1, files: 1, bytes: 1024), kept: 2, ExitCodes.Success));

        root.GetProperty("operation").GetString().Should().Be("prune-backups");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("backupDir").GetString().Should().Be("/backups");
        root.GetProperty("status").GetString().Should().Be("pruned");
    }

    [Fact]
    public void Serialize_SummaryFoldsTheTotals()
    {
        var root = Parse(Serialize("prune-backups", "pruned", found: 3, Result(removed: 1, files: 2, bytes: 2048), kept: 2, ExitCodes.Success));
        var summary = root.GetProperty("summary");

        summary.GetProperty("found").GetInt32().Should().Be(3);
        summary.GetProperty("removed").GetInt32().Should().Be(1);
        summary.GetProperty("kept").GetInt32().Should().Be(2);
        summary.GetProperty("filesRemoved").GetInt32().Should().Be(2);
        summary.GetProperty("bytesFreed").GetInt64().Should().Be(2048);
    }

    [Fact]
    public void Serialize_NoBackups_CarriesTheVerdictAsData()
    {
        // The console path reports an empty history as a line and exits 0; the document carries
        // the same verdict as a field, so a consumer can tell "nothing to delete" apart from
        // "deleted nothing because it failed".
        var root = Parse(Serialize("prune-backups", "noBackups", found: 0, null, kept: 0, ExitCodes.Success));

        root.GetProperty("status").GetString().Should().Be("noBackups");
        root.GetProperty("summary").GetProperty("found").GetInt32().Should().Be(0);
        root.GetProperty("errors").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Serialize_NothingToPrune_CarriesTheVerdictAsData()
    {
        var root = Parse(Serialize("prune-backups", "nothingToPrune", found: 2, null, kept: 2, ExitCodes.Success));

        root.GetProperty("status").GetString().Should().Be("nothingToPrune");
        root.GetProperty("summary").GetProperty("kept").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Serialize_Errors_CarryTheFailures()
    {
        var result = Result(removed: 1, files: 1, bytes: 512);
        result.Errors.Add("could not delete App.csproj.backup_20240101: file locked");

        var root = Parse(Serialize("prune-all-backups", "failed", found: 2, result, kept: 0, ExitCodes.FileOperationError));

        root.GetProperty("operation").GetString().Should().Be("prune-all-backups");
        root.GetProperty("status").GetString().Should().Be("failed");
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.FileOperationError);
        root.GetProperty("errors").GetArrayLength().Should().Be(1);
    }

    private static string Serialize(string operation, string status, int found, PruneResult? result, int kept, int exitCode) =>
        PruneBackupsJsonWriter.Serialize(operation, "/backups", status, found, result, kept, exitCode);

    private static PruneResult Result(int removed, int files, long bytes) =>
        new() { BackupsRemoved = removed, FilesRemoved = files, BytesFreed = bytes };

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
