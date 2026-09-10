using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--prune-backups</c> and <c>--prune-all-backups</c>: the same
/// verdict the console prints — what was found, what was removed, what went wrong — serialized
/// as one JSON document so a CI job can prune backups without parsing prose.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — <c>prune-backups</c> or <c>prune-all-backups</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="BackupDir">The directory the history was read from, as resolved.</param>
/// <param name="Status">
/// The outcome as a single token: <c>pruned</c> (sets removed), <c>noBackups</c> (nothing to
/// delete), <c>nothingToPrune</c> (every set is inside the retention window), or <c>failed</c>
/// (errors during deletion). A machine-readable run without <c>--force</c> is rejected by
/// validation before the handler runs, so <c>refused</c> is not a status this document can carry.
/// </param>
public sealed record PruneBackupsReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("backupDir")] string BackupDir,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("summary")] PruneBackupsSummaryPayload Summary,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors
);

/// <summary>
/// The totals the console prints below the banner.
/// </summary>
/// <param name="Found">Backup sets present before the prune.</param>
/// <param name="Removed">Backup sets deleted.</param>
/// <param name="Kept">Backup sets left in place.</param>
/// <param name="FilesRemoved">Files deleted across the removed sets.</param>
/// <param name="BytesFreed">Bytes the removed sets held.</param>
public sealed record PruneBackupsSummaryPayload(
    [property: JsonPropertyName("found")] int Found,
    [property: JsonPropertyName("removed")] int Removed,
    [property: JsonPropertyName("kept")] int Kept,
    [property: JsonPropertyName("filesRemoved")] int FilesRemoved,
    [property: JsonPropertyName("bytesFreed")] long BytesFreed
);

/// <summary>
/// Serializes a prune outcome into the single JSON document the
/// <c>--prune-backups --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share the outcome, never output.
/// </summary>
internal static class PruneBackupsJsonWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the payload. <paramref name="exitCode"/> is the value the process
    /// settled on — passed in rather than recomputed so the document cannot disagree with it.
    /// </summary>
    public static string Serialize(
        string operation,
        string backupDir,
        string status,
        int found,
        PruneResult? result,
        int kept,
        int exitCode
    )
    {
        var payload = new PruneBackupsReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            operation,
            exitCode,
            backupDir,
            status,
            new PruneBackupsSummaryPayload(
                found,
                result?.BackupsRemoved ?? 0,
                kept,
                result?.FilesRemoved ?? 0,
                result?.BytesFreed ?? 0
            ),
            result?.Errors ?? (IReadOnlyList<string>)[]
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
