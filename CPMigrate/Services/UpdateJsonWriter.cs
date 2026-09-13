using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--update</c>: the same verdict the console prints — what the
/// running binary is, what the feed offered, and what was done about it — serialized as one JSON
/// document so a CI job can self-update the tool in a pipeline step and gate on the outcome
/// without parsing prose.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>update</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="Status">
/// The outcome as a single token — see <see cref="SelfUpdateResult.Status"/> for the set.
/// <c>nonInteractive</c> is the one a pipeline most needs to read: it means a newer version exists
/// and the run could not ask for consent, so re-running with <c>--force</c> is the next step.
/// </param>
/// <param name="DryRun">Whether <c>--dry-run</c> was passed — the check ran, nothing was installed.</param>
/// <param name="CurrentVersion">The version of the running binary.</param>
/// <param name="LatestVersion">
/// The newest stable version the feed reported. Absent rather than null when the check itself
/// failed — a consumer cannot act on a version nobody saw.
/// </param>
/// <param name="Forced">
/// Whether <c>--force</c> was passed — what distinguishes an unattended update from a run that
/// could only report <c>nonInteractive</c>.
/// </param>
/// <param name="Error">
/// What a <c>failed</c> update attempt reported — <c>dotnet tool update</c>'s stderr, or the
/// exception message when it could not be started. Absent on every other status.
/// </param>
public sealed record UpdateReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("currentVersion")] string CurrentVersion,
    [property: JsonPropertyName("latestVersion")] string? LatestVersion,
    [property: JsonPropertyName("forced")] bool Forced,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("error")] string? Error
);

/// <summary>
/// Serializes a self-update outcome into the single JSON document the
/// <c>--update --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share the outcome, never output.
/// </summary>
internal static class UpdateJsonWriter
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
    public static string Serialize(SelfUpdateResult result, bool forced, bool dryRun, int exitCode)
    {
        var payload = new UpdateReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "update",
            exitCode,
            result.Status,
            result.CurrentVersion,
            result.LatestVersion,
            forced,
            dryRun,
            result.Error
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
