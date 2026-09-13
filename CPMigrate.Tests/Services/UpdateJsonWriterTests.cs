using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--update --output Json</c> promises CI scripts one parseable document carrying the same
/// verdict the console prints — the versions involved and what was done about them — so a
/// pipeline step can self-update the tool and gate on the outcome without parsing prose.
/// </summary>
public class UpdateJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Serialize(new SelfUpdateResult("updated", "3.65.0", "3.66.0"));

        root.GetProperty("operation").GetString().Should().Be("update");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("status").GetString().Should().Be("updated");
        root.GetProperty("currentVersion").GetString().Should().Be("3.65.0");
        root.GetProperty("latestVersion").GetString().Should().Be("3.66.0");
        root.GetProperty("forced").GetBoolean().Should().BeFalse();
        root.GetProperty("dryRun").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Serialize_DryRun_CarriesTheVersionItDidNotInstall()
    {
        // --dry-run reports what --update would do, so the available version is the whole point
        // of the document — and success, because the ask was a check and the check answered.
        var root = Serialize(
            new SelfUpdateResult("dryRun", "3.65.0", "3.66.0"),
            ExitCodes.Success,
            dryRun: true
        );

        root.GetProperty("status").GetString().Should().Be("dryRun");
        root.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        root.GetProperty("latestVersion").GetString().Should().Be("3.66.0");
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void Serialize_CheckFailed_OmitsTheVersionNobodySaw()
    {
        // latestVersion is absent rather than null: a consumer cannot act on a version the feed
        // never reported, and a missing key reads differently from an invented one.
        var root = Serialize(new SelfUpdateResult("checkFailed", "3.65.0"), ExitCodes.UnexpectedError);

        root.GetProperty("status").GetString().Should().Be("checkFailed");
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.UnexpectedError);
        root.TryGetProperty("latestVersion", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_NonInteractive_CarriesBothVersions()
    {
        // The verdict a pipeline most needs: a newer version exists and the run could not ask for
        // consent — both versions are data so the consumer can report or act on the gap.
        var root = Serialize(
            new SelfUpdateResult("nonInteractive", "3.65.0", "3.66.0"),
            ExitCodes.UnexpectedError
        );

        root.GetProperty("status").GetString().Should().Be("nonInteractive");
        root.GetProperty("currentVersion").GetString().Should().Be("3.65.0");
        root.GetProperty("latestVersion").GetString().Should().Be("3.66.0");
    }

    [Fact]
    public void Serialize_Failed_CarriesWhatTheAttemptSaid()
    {
        var root = Serialize(
            new SelfUpdateResult("failed", "3.65.0", "3.66.0", "tool exited 1"),
            ExitCodes.UnexpectedError
        );

        root.GetProperty("status").GetString().Should().Be("failed");
        root.GetProperty("error").GetString().Should().Be("tool exited 1");
    }

    [Fact]
    public void Serialize_OmitsErrorOnEveryOtherStatus()
    {
        foreach (var status in new[] { "updated", "alreadyLatest", "checkFailed", "dryRun", "nonInteractive", "declined" })
        {
            var root = Serialize(new SelfUpdateResult(status, "3.65.0", "3.66.0"));
            root.TryGetProperty("error", out _).Should().BeFalse($"error is only meaningful on failed, not {status}");
        }
    }

    [Fact]
    public void Serialize_CarriesForced()
    {
        var root = Serialize(new SelfUpdateResult("updated", "3.65.0", "3.66.0"), forced: true);

        root.GetProperty("forced").GetBoolean().Should().BeTrue();
    }

    private static JsonElement Serialize(
        SelfUpdateResult result,
        int exitCode = ExitCodes.Success,
        bool forced = false,
        bool dryRun = false
    ) => JsonDocument.Parse(UpdateJsonWriter.Serialize(result, forced, dryRun, exitCode)).RootElement;
}
