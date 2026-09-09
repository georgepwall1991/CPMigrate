using System.Text.Json;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Telemetry is opt-in and local-only, and that promise has to survive refactors: disabled by
/// default, enabled only by an explicit value, and the payload carrying operational facts —
/// never repository, package, or file content.
/// </summary>
public class TelemetryServiceTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previousOptIn;
    private readonly string? _previousHome;

    public TelemetryServiceTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "cpmigrate-telemetry-" + Guid.NewGuid().ToString("N"));
        // GetFolderPath(UserProfile) resolves empty unless the directory exists, so the
        // sandbox home must be real before anything reads it.
        Directory.CreateDirectory(_home);
        _previousOptIn = Environment.GetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN");
        _previousHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", _home);
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", _previousOptIn);
        Environment.SetEnvironmentVariable("HOME", _previousHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch
        {
            // Best effort: a failed test must not leave the next run reading stale events.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RecordCommandRun_DisabledByDefault_WritesNothing()
    {
        TelemetryService.RecordCommandRun(new Options(), ExitCodes.Success, TimeSpan.FromSeconds(1));

        Directory.Exists(Path.Combine(_home, ".cpmigrate")).Should().BeFalse("telemetry is opt-in");
    }

    [Fact]
    public void RecordCommandRun_UnresolvableHome_WritesNothingAndThrowsNothing()
    {
        // Containers and service accounts can have HOME unset or pointing nowhere, in which
        // case the profile lookup resolves empty. That must be silence, not a .cpmigrate
        // directory scattered into whatever the working directory happens to be.
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", "1");
        Environment.SetEnvironmentVariable("HOME", Path.Combine(_home, "does-not-exist"));
        var cwdMarker = Path.Combine(Directory.GetCurrentDirectory(), ".cpmigrate");
        var existed = Directory.Exists(cwdMarker);

        var act = () => TelemetryService.RecordCommandRun(new Options(), ExitCodes.Success, TimeSpan.FromSeconds(1));

        act.Should().NotThrow();
        Directory.Exists(cwdMarker).Should().Be(existed, "telemetry must never scatter into the working directory");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2")]
    public void RecordCommandRun_WithoutExplicitOptIn_WritesNothing(string value)
    {
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", value);

        TelemetryService.RecordCommandRun(new Options(), ExitCodes.Success, TimeSpan.FromSeconds(1));

        Directory.Exists(Path.Combine(_home, ".cpmigrate")).Should().BeFalse($"'{value}' is not an opt-in");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("TRUE")]
    [InlineData("Yes")]
    public void RecordCommandRun_WithExplicitOptIn_AppendsOneEvent(string value)
    {
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", value);

        TelemetryService.RecordCommandRun(new Options { Analyze = true }, ExitCodes.Success, TimeSpan.FromSeconds(2));

        var line = ReadSingleEvent();
        var payload = JsonDocument.Parse(line).RootElement;
        payload.GetProperty("operation").GetString().Should().Be("analyze");
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        payload.GetProperty("exitCodeCategory").GetString().Should().Be("success");
        payload.GetProperty("durationMs").GetInt64().Should().Be(2000);
        payload.TryGetProperty("flags", out _).Should().BeTrue();
    }

    [Fact]
    public void RecordCommandRun_FailedRun_RecordsFailureCategory()
    {
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", "1");

        TelemetryService.RecordCommandRun(new Options { UpdatePackages = true }, ExitCodes.TestFailure, TimeSpan.Zero);

        var payload = JsonDocument.Parse(ReadSingleEvent()).RootElement;
        payload.GetProperty("operation").GetString().Should().Be("update-packages");
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("exitCodeCategory").GetString().Should().Be("test-failure");
    }

    [Fact]
    public void RecordCommandRun_PayloadContainsNoRepositoryContent()
    {
        // The privacy contract: operational facts only. Package IDs, paths, and versions
        // must never appear in the event.
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", "1");

        TelemetryService.RecordCommandRun(
            new Options { Analyze = true, SolutionFileDir = "/secret/acme" },
            ExitCodes.Success,
            TimeSpan.FromSeconds(1));

        var line = ReadSingleEvent();
        line.Should().NotContain("acme");
        line.Should().NotContain("/secret");
        line.Should().NotContain(".sln");
    }

    private string ReadSingleEvent()
    {
        var file = Path.Combine(_home, ".cpmigrate", "telemetry", "events.ndjson");
        File.Exists(file).Should().BeTrue("opting in must record the run");
        var lines = File.ReadAllLines(file);
        lines.Should().HaveCount(1);
        return lines[0];
    }
}
