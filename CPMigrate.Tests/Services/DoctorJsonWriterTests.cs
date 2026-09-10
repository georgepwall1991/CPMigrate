using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--doctor --output Json</c> promises CI scripts one parseable document carrying the same
/// check list the console table renders — name, status, details, hint — plus the counts the exit
/// code folds, so a gate can read which check failed rather than only the exit code.
/// </summary>
public class DoctorJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Parse(Serialize(Checks()));

        root.GetProperty("operation").GetString().Should().Be("doctor");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void Serialize_ChecksCarryNameStatusDetails()
    {
        var root = Parse(Serialize(Checks()));
        var check = root.GetProperty("checks")[0];

        check.GetProperty("name").GetString().Should().Be("SDK");
        check.GetProperty("status").GetString().Should().Be("ok");
        check.GetProperty("details").GetString().Should().Be(".NET SDK 10.0.0");
    }

    [Fact]
    public void Serialize_HintlessCheck_OmitsTheHint()
    {
        // A null hint must not serialize as "hint": null — absent is the contract, so a consumer
        // checking for the field's presence gets the honest answer.
        var root = Parse(Serialize(Checks()));
        var check = root.GetProperty("checks")[0];

        check.TryGetProperty("hint", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_HintedCheck_CarriesTheHint()
    {
        var root = Parse(Serialize(Checks()));
        var check = root.GetProperty("checks")[1];

        check.GetProperty("hint").GetString().Should().Be("Free up space first.");
    }

    [Fact]
    public void Serialize_SummaryFoldsTheCountsTheExitCodeFolds()
    {
        var root = Parse(Serialize(Checks(), ExitCodes.UnexpectedError));
        var summary = root.GetProperty("summary");

        summary.GetProperty("total").GetInt32().Should().Be(3);
        summary.GetProperty("errors").GetInt32().Should().Be(1);
        summary.GetProperty("warnings").GetInt32().Should().Be(1);
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.UnexpectedError);
    }

    [Fact]
    public void Serialize_StatusNames_AreLowercase()
    {
        var root = Parse(Serialize(Checks()));
        var statuses = root
            .GetProperty("checks")
            .EnumerateArray()
            .Select(c => c.GetProperty("status").GetString())
            .ToList();

        statuses.Should().BeEquivalentTo("ok", "warning", "error");
    }

    private static string Serialize(IReadOnlyList<DoctorCheck> checks, int exitCode = ExitCodes.Success) =>
        DoctorJsonWriter.Serialize(checks, exitCode);

    private static List<DoctorCheck> Checks() =>
    [
        new DoctorCheck("SDK", DoctorStatus.Ok, ".NET SDK 10.0.0"),
        new DoctorCheck("Disk", DoctorStatus.Warning, "1 GB free", "Free up space first."),
        new DoctorCheck("Workspace", DoctorStatus.Error, "Not writable", "Check directory ACLs."),
    ];

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
