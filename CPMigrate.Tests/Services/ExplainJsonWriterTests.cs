using System.Text.Json;
using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--explain --output Json</c> promises CI scripts and IDE extensions one parseable document
/// carrying the same rule the console describes — or every rule for <c>--explain all</c>, or the
/// near-miss suggestions for an unmatched ID.
/// </summary>
public class ExplainJsonWriterTests
{
    [Fact]
    public void Serialize_SingleRule_EmitsTheDocumentShape()
    {
        var rule = AnalysisRuleCatalog.Get(AnalysisIssueCode.VersionInconsistency);
        var root = Parse(Serialize("VersionInconsistency", rule, null, null, ExitCodes.Success));

        root.GetProperty("operation").GetString().Should().Be("explain");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("query").GetString().Should().Be("VersionInconsistency");
        root.GetProperty("found").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Serialize_SingleRule_CarriesTheCatalogFields()
    {
        var rule = AnalysisRuleCatalog.Get(AnalysisIssueCode.VersionInconsistency);
        var root = Parse(Serialize("VersionInconsistency", rule, null, null, ExitCodes.Success));
        var payload = root.GetProperty("rule");

        payload.GetProperty("id").GetString().Should().Be("VersionInconsistency");
        payload.GetProperty("shortDescription").GetString().Should().Be(rule.ShortDescription);
        payload.GetProperty("fullDescription").GetString().Should().Be(rule.FullDescription);
        payload.GetProperty("helpUri").GetString().Should().Be(rule.HelpUri);
        payload.GetProperty("tags").GetArrayLength().Should().Be(rule.Tags.Count);
    }

    [Fact]
    public void Serialize_All_CarriesEveryRule()
    {
        var root = Parse(Serialize("all", null, AnalysisRuleCatalog.All, null, ExitCodes.Success));

        root.GetProperty("found").GetBoolean().Should().BeTrue();
        root.GetProperty("rules").GetArrayLength().Should().Be(AnalysisRuleCatalog.All.Count);
        root.TryGetProperty("rule", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_Unknown_CarriesTheSuggestions()
    {
        // The console path prints "Did you mean: …" as prose; the document carries the same
        // near-misses as a field, so a consumer can offer them without parsing the text.
        var root = Parse(Serialize("VersionInconsistenc", null, null, new[] { "VersionInconsistency" }, ExitCodes.ValidationError));

        root.GetProperty("found").GetBoolean().Should().BeFalse();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.ValidationError);
        root.GetProperty("suggestions")[0].GetString().Should().Be("VersionInconsistency");
        root.TryGetProperty("rule", out _).Should().BeFalse();
    }

    private static string Serialize(string query, AnalysisRule? rule, IReadOnlyList<AnalysisRule>? allRules, IReadOnlyList<string>? suggestions, int exitCode) =>
        ExplainJsonWriter.Serialize(query, rule, allRules, suggestions, exitCode);

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
