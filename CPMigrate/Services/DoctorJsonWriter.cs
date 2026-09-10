using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--doctor</c>: the same environment checks the console table
/// renders, serialized as one JSON document so a CI script can gate on which check failed rather
/// than on the exit code alone.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>doctor</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="Checks">Every check that ran, in report order.</param>
/// <param name="Summary">How the checks landed — the counts the exit code folds.</param>
public sealed record DoctorReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("checks")] IReadOnlyList<DoctorCheckPayload> Checks,
    [property: JsonPropertyName("summary")] DoctorSummaryPayload Summary
);

/// <summary>
/// One doctor check as data. <paramref name="Status"/> is the lowercase severity word —
/// <c>ok</c>, <c>info</c>, <c>warning</c>, or <c>error</c> — matching the vocabulary the rest of
/// the JSON contract already uses.
/// </summary>
/// <param name="Name">The check's stable name (SDK, NuGet, Workspace, …).</param>
/// <param name="Status">The check's verdict.</param>
/// <param name="Details">What the check found, as the console table would render it.</param>
/// <param name="Hint">What to do about a non-ok verdict; absent when the check has none.</param>
public sealed record DoctorCheckPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("hint")] string? Hint
);

/// <summary>
/// The counts the exit code folds: any error means the run exits non-zero, warnings alone do not.
/// </summary>
/// <param name="Total">Checks that ran.</param>
/// <param name="Errors">Checks with status <c>error</c>.</param>
/// <param name="Warnings">Checks with status <c>warning</c>.</param>
public sealed record DoctorSummaryPayload(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("errors")] int Errors,
    [property: JsonPropertyName("warnings")] int Warnings
);

/// <summary>
/// Serializes doctor's check list into the single JSON document the
/// <c>--doctor --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share the check list, never output.
/// </summary>
internal static class DoctorJsonWriter
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
    public static string Serialize(IReadOnlyList<DoctorCheck> checks, int exitCode)
    {
        var payload = new DoctorReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "doctor",
            exitCode,
            checks
                .Select(check => new DoctorCheckPayload(
                    check.Name,
                    StatusName(check.Status),
                    check.Details,
                    check.Hint
                ))
                .ToList(),
            new DoctorSummaryPayload(
                checks.Count,
                checks.Count(c => c.Status == DoctorStatus.Error),
                checks.Count(c => c.Status == DoctorStatus.Warning)
            )
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static string StatusName(DoctorStatus status) =>
        status switch
        {
            DoctorStatus.Ok => "ok",
            DoctorStatus.Info => "info",
            DoctorStatus.Warning => "warning",
            DoctorStatus.Error => "error",
            _ => status.ToString().ToLowerInvariant(),
        };
}
