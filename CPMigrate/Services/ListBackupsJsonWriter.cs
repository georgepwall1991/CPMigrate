using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--list-backups</c>: the same backup history the console table
/// renders, serialized as one JSON document so a CI script can prune or verify backups without
/// parsing prose.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>list-backups</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="BackupDir">The directory the history was read from, as resolved.</param>
/// <param name="DirectoryExists">
/// Whether the backup directory exists at all. The console path reports a missing directory as a
/// warning and exits 0; the document carries the same verdict as data, so a consumer can tell
/// "no backups yet" apart from "no backup directory".
/// </param>
/// <param name="Backups">Every backup set found, newest first — the table's row order.</param>
/// <param name="Summary">The totals the console prints below the table.</param>
public sealed record ListBackupsReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("backupDir")] string BackupDir,
    [property: JsonPropertyName("directoryExists")] bool DirectoryExists,
    [property: JsonPropertyName("backups")] IReadOnlyList<BackupSetPayload> Backups,
    [property: JsonPropertyName("summary")] ListBackupsSummaryPayload Summary
);

/// <summary>
/// One backup set as data. <paramref name="Timestamp"/> is the directory name the backup was
/// created under — the value <c>--rollback</c> takes — not a reformatted date.
/// </summary>
/// <param name="Timestamp">The backup set's directory name.</param>
/// <param name="Files">How many files the set holds.</param>
/// <param name="SizeBytes">Total size of the files that still exist on disk.</param>
/// <param name="FileNames">The backed-up file paths.</param>
public sealed record BackupSetPayload(
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("fileNames")] IReadOnlyList<string> FileNames
);

/// <summary>
/// The totals the console prints below the table.
/// </summary>
/// <param name="Sets">Backup sets found.</param>
/// <param name="Files">Files across every set.</param>
/// <param name="SizeBytes">Bytes across every set.</param>
public sealed record ListBackupsSummaryPayload(
    [property: JsonPropertyName("sets")] int Sets,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes
);

/// <summary>
/// Serializes a backup history into the single JSON document the
/// <c>--list-backups --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share the history, never output.
/// </summary>
internal static class ListBackupsJsonWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the payload. <paramref name="exitCode"/> is the value the process
    /// settled on — passed in rather than recomputed so the document cannot disagree with it.
    /// <paramref name="directoryExists"/> carries the console path's missing-directory verdict as
    /// data rather than as a warning a JSON consumer would never see.
    /// </summary>
    public static string Serialize(
        string backupDir,
        bool directoryExists,
        IReadOnlyList<BackupSetInfo> backups,
        int exitCode
    )
    {
        var sets = backups
            .Select(backup => new BackupSetPayload(
                backup.Timestamp,
                backup.Files.Count,
                CalculateSize(backup.Files),
                backup.Files
            ))
            .ToList();

        var payload = new ListBackupsReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "list-backups",
            exitCode,
            backupDir,
            directoryExists,
            sets,
            new ListBackupsSummaryPayload(
                sets.Count,
                sets.Sum(s => s.Files),
                sets.Sum(s => s.SizeBytes)
            )
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static long CalculateSize(IReadOnlyList<string> files)
    {
        return files.Sum(f =>
        {
            try
            {
                return File.Exists(f) ? new FileInfo(f).Length : 0;
            }
            catch
            {
                return 0;
            }
        });
    }
}
