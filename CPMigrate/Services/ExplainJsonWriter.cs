using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--explain</c>: the same rule the console describes —
/// or every rule for <c>--explain all</c> — serialized as one JSON document so an IDE
/// extension or CI script can read rule metadata without parsing prose.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>explain</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="Query">The rule ID or <c>all</c> the run was asked about, verbatim.</param>
/// <param name="Found">Whether the query matched a rule — false carries <c>suggestions</c>.</param>
/// <param name="Rule">The matched rule, or null when the query named nothing.</param>
/// <param name="Rules">Every rule, for <c>--explain all</c> — null for a single-rule query.</param>
/// <param name="Suggestions">Near-miss rule IDs for an unmatched query — null when the query matched.</param>
public sealed record ExplainReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("rule")] ExplainRulePayload? Rule,
    [property: JsonPropertyName("rules")] IReadOnlyList<ExplainRulePayload>? Rules,
    [property: JsonPropertyName("suggestions")] IReadOnlyList<string>? Suggestions
);

/// <summary>
/// One rule as data — the fields the console describes, as fields a consumer can read.
/// </summary>
/// <param name="Id">The stable rule identifier — the value <c>issueCode</c> carries in JSON findings and <c>ruleId</c> carries in SARIF.</param>
/// <param name="ShortDescription">The one-line summary.</param>
/// <param name="FullDescription">The full explanation.</param>
/// <param name="Tags">Classification tags, as SARIF consumers see them.</param>
/// <param name="HelpUri">Anchor into the published rule documentation.</param>
public sealed record ExplainRulePayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("shortDescription")] string ShortDescription,
    [property: JsonPropertyName("fullDescription")] string FullDescription,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("helpUri")] string HelpUri
);

/// <summary>
/// Serializes an explain outcome into the single JSON document the
/// <c>--explain --output Json</c> contract promises on stdout. Separate from the console
/// rendering, which stays untouched: the two paths share the catalog, never output.
/// </summary>
internal static class ExplainJsonWriter
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
        string query,
        AnalysisRule? rule,
        IReadOnlyList<AnalysisRule>? allRules,
        IReadOnlyList<string>? suggestions,
        int exitCode
    )
    {
        var payload = new ExplainReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "explain",
            exitCode,
            query,
            Found: rule is not null || allRules is not null,
            rule is null ? null : ToPayload(rule),
            allRules?.Select(ToPayload).ToList(),
            suggestions
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static ExplainRulePayload ToPayload(AnalysisRule rule) =>
        new(rule.Id, rule.ShortDescription, rule.FullDescription, rule.Tags, rule.HelpUri);
}
