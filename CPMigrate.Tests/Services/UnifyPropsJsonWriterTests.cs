using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--unify-props --output Json</c> promises CI scripts one parseable document carrying the
/// same verdict the console prints — which properties and items met the consensus threshold, and
/// what was done about them — so a consumer can gate on the outcome without parsing prose.
/// </summary>
public class UnifyPropsJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Parse(Serialize("unified", forced: true, ExitCodes.Success));

        root.GetProperty("operation").GetString().Should().Be("unify-props");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("propsFilePath").GetString().Should().Be("/repo/Directory.Build.props");
        root.GetProperty("status").GetString().Should().Be("unified");
        root.GetProperty("forced").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Serialize_Refused_CarriesTheRefusalAsData()
    {
        // The console path reports a non-interactive refusal as a warning and exits 0; the
        // document carries the same verdict as a field, so a consumer can tell "wrote nothing"
        // apart from "wrote it".
        var root = Parse(Serialize("refused", forced: false, ExitCodes.Success));

        root.GetProperty("status").GetString().Should().Be("refused");
        root.GetProperty("forced").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Serialize_NoCandidates_CarriesEmptyCandidates()
    {
        var root = Parse(Serialize("noCandidates", forced: false, ExitCodes.Success));

        root.GetProperty("status").GetString().Should().Be("noCandidates");
        root.GetProperty("candidates").GetProperty("properties").GetArrayLength().Should().Be(0);
        root.GetProperty("candidates").GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Serialize_DryRun_CarriesCandidatesWithoutWriting()
    {
        var root = Parse(Serialize("dryRun", forced: false, ExitCodes.Success));

        root.GetProperty("status").GetString().Should().Be("dryRun");
        root.GetProperty("candidates").GetProperty("properties").GetArrayLength().Should().Be(1);
        root.GetProperty("summary").GetProperty("filesModified").GetInt32().Should().Be(0);
    }

    private static string Serialize(string status, bool forced, int exitCode) =>
        UnifyPropsJsonWriter.Serialize(
            "/repo/Directory.Build.props",
            status,
            forced,
            exitCode,
            new UnifyPropsCandidatesPayload(
                status == "noCandidates"
                    ? []
                    : [new UnifyPropsPropertyPayload("Nullable", "enable", 2, ["/repo/Api.csproj", "/repo/Lib.csproj"])],
                []
            ),
            new UnifyPropsSummaryPayload(2, status == "noCandidates" ? 0 : 1, 0, status == "unified" ? 3 : 0)
        );

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
