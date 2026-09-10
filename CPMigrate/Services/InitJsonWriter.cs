using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--init</c>: the same verdict the console prints — whether the
/// config file was written, and where — serialized as one JSON document so a CI script can
/// scaffold a config without parsing prose.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>init</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="ConfigPath">The file the run resolved to write, as resolved.</param>
/// <param name="Status">
/// The outcome as a single token: <c>created</c> (the file was written where none existed),
/// <c>overwritten</c> (<c>--force</c> replaced an existing file), or <c>exists</c> (a file was
/// already there and <c>--force</c> was not passed — the refusal the console prints as a warning,
/// carried as data so a consumer can tell "wrote nothing" apart from "wrote it").
/// </param>
/// <param name="Forced">Whether <c>--force</c> was passed — what distinguishes an overwrite from a refusal.</param>
public sealed record InitReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("configPath")] string ConfigPath,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("forced")] bool Forced
);

/// <summary>
/// Serializes an init outcome into the single JSON document the
/// <c>--init --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share the outcome, never output.
/// </summary>
internal static class InitJsonWriter
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
    public static string Serialize(string configPath, string status, bool forced, int exitCode)
    {
        var payload = new InitReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "init",
            exitCode,
            configPath,
            status,
            forced
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
