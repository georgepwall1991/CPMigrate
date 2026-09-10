using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--unify-props</c>: the same verdict the console prints —
/// which properties and items met the consensus threshold, and what was done about them —
/// serialized as one JSON document so a CI script can gate on the outcome without parsing prose.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>unify-props</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="PropsFilePath">The Directory.Build.props the run resolved to write, as resolved.</param>
/// <param name="Status">
/// The outcome as a single token: <c>unified</c> (the props file was written and the matching
/// projects were stripped), <c>dryRun</c> (<c>--dry-run</c> preview — nothing was written),
/// <c>noCandidates</c> (nothing met the consensus threshold), <c>refused</c> (candidates existed
/// but the run could not confirm the write — non-interactive without <c>--force</c>), or
/// <c>failed</c> (an error stopped the run).
/// </param>
/// <param name="Forced">Whether <c>--force</c> was passed — what distinguishes a write from a refusal.</param>
/// <param name="Candidates">The properties and items that met the consensus threshold.</param>
/// <param name="Summary">Totals a gate can read without walking the candidate list.</param>
public sealed record UnifyPropsReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("propsFilePath")] string PropsFilePath,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("forced")] bool Forced,
    [property: JsonPropertyName("candidates")] UnifyPropsCandidatesPayload Candidates,
    [property: JsonPropertyName("summary")] UnifyPropsSummaryPayload Summary
);

/// <summary>
/// The properties and items that met the consensus threshold, grouped by kind.
/// </summary>
public sealed record UnifyPropsCandidatesPayload(
    [property: JsonPropertyName("properties")] IReadOnlyList<UnifyPropsPropertyPayload> Properties,
    [property: JsonPropertyName("items")] IReadOnlyList<UnifyPropsItemPayload> Items
);

/// <summary>
/// A property that met the consensus threshold.
/// </summary>
/// <param name="Name">The MSBuild property name.</param>
/// <param name="Value">The value the consensus projects share.</param>
/// <param name="Count">How many projects declare it.</param>
/// <param name="Projects">The projects that declare it, relative to the scan root.</param>
public sealed record UnifyPropsPropertyPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects
);

/// <summary>
/// An item that met the consensus threshold.
/// </summary>
/// <param name="ItemType">The MSBuild item type (e.g. <c>Using</c>).</param>
/// <param name="Include">The item's <c>Include</c> value.</param>
/// <param name="Count">How many projects declare it.</param>
/// <param name="Projects">The projects that declare it, relative to the scan root.</param>
/// <param name="Metadata">The item's metadata, when it carries any.</param>
public sealed record UnifyPropsItemPayload(
    [property: JsonPropertyName("itemType")] string ItemType,
    [property: JsonPropertyName("include")] string Include,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata
);

/// <summary>
/// Totals a gate can read without walking the candidate list.
/// </summary>
/// <param name="TotalProjects">Projects scanned.</param>
/// <param name="PropertiesUnified">Properties that met the threshold.</param>
/// <param name="ItemsUnified">Items that met the threshold.</param>
/// <param name="FilesModified">Files the run wrote — the props file plus every stripped project.</param>
public sealed record UnifyPropsSummaryPayload(
    [property: JsonPropertyName("totalProjects")] int TotalProjects,
    [property: JsonPropertyName("propertiesUnified")] int PropertiesUnified,
    [property: JsonPropertyName("itemsUnified")] int ItemsUnified,
    [property: JsonPropertyName("filesModified")] int FilesModified
);

/// <summary>
/// Serializes a unify-props outcome into the single JSON document the
/// <c>--unify-props --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share the outcome, never output.
/// </summary>
internal static class UnifyPropsJsonWriter
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
        string propsFilePath,
        string status,
        bool forced,
        int exitCode,
        UnifyPropsCandidatesPayload candidates,
        UnifyPropsSummaryPayload summary
    )
    {
        var payload = new UnifyPropsReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "unify-props",
            exitCode,
            propsFilePath,
            status,
            forced,
            candidates,
            summary
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
