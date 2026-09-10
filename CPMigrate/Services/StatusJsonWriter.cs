using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--status</c>: the same workspace facts the console dashboard
/// renders, serialized as one JSON document so a CI script can consume them.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>status</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="Directory">The directory the status describes, as resolved.</param>
/// <param name="Solutions">Solution file names found in that directory.</param>
/// <param name="ProjectCount">Project files found recursively, excluding bin/obj output.</param>
/// <param name="CpmEnabled">Whether a Directory.Packages.props exists.</param>
/// <param name="CentralPackageCount">
/// PackageVersion entries in the props file; absent when CPM is not enabled or the file could not
/// be read.
/// </param>
/// <param name="ConfigPresent">Whether a .cpmigrate.json exists.</param>
/// <param name="GitRepository">Whether the directory is a git repository.</param>
/// <param name="GitDirty">Whether the repository has unstaged changes; absent when not a repo.</param>
/// <param name="BackupSets">Backup sets under .cpmigrate_backup.</param>
/// <param name="TargetFrameworks">Target framework to project count, over top-level project files.</param>
public sealed record StatusReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("solutions")] IReadOnlyList<string> Solutions,
    [property: JsonPropertyName("projectCount")] int ProjectCount,
    [property: JsonPropertyName("cpmEnabled")] bool CpmEnabled,
    [property: JsonPropertyName("centralPackageCount")] int? CentralPackageCount,
    [property: JsonPropertyName("configPresent")] bool ConfigPresent,
    [property: JsonPropertyName("gitRepository")] bool GitRepository,
    [property: JsonPropertyName("gitDirty")] bool? GitDirty,
    [property: JsonPropertyName("backupSets")] int BackupSets,
    [property: JsonPropertyName("targetFrameworks")] IReadOnlyDictionary<string, int> TargetFrameworks
);

/// <summary>
/// Serializes a <see cref="WorkspaceStatus"/> into the single JSON document the
/// <c>--status --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share collection, never output.
/// </summary>
internal static class StatusJsonWriter
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
    public static string Serialize(WorkspaceStatus status, int exitCode)
    {
        var payload = new StatusReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "status",
            exitCode,
            status.Directory,
            status.Solutions,
            status.ProjectCount,
            status.CpmEnabled,
            status.CpmEnabled ? status.CentralPackageCount : null,
            status.ConfigPresent,
            status.GitRepository,
            status.GitRepository ? status.GitDirty : null,
            status.BackupSets,
            status.TargetFrameworks
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
