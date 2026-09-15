using System.Text.Json;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Telemetry is opt-in and local-only, and that promise has to survive refactors: disabled by
/// default, enabled only by an explicit value, and the payload carrying operational facts —
/// never repository, package, or file content. The destination is injected, so no test depends
/// on how the OS resolves the user profile (<c>HOME</c> on Unix, the profile lookup on Windows).
/// </summary>
public class TelemetryServiceTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previousOptIn;

    public TelemetryServiceTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "cpmigrate-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _previousOptIn = Environment.GetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN");
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", _previousOptIn);
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

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("TRUE", true)]
    [InlineData("Yes", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("2", false)]
    public void IsEnabled_OnlyExplicitOptInValuesEnable(string? value, bool expected)
    {
        Environment.SetEnvironmentVariable("CPMIGRATE_TELEMETRY_OPT_IN", value);

        TelemetryService.IsEnabled().Should().Be(expected);
    }

    [Fact]
    public void RecordCommandRun_DisabledByDefault_DoesNotThrow()
    {
        // The gate is closed, so this returns before touching the filesystem — safe to run
        // against the real profile resolution on any OS.
        var act = () => TelemetryService.RecordCommandRun(new Options(), ExitCodes.Success, TimeSpan.FromSeconds(1));

        act.Should().NotThrow();
    }

    [Fact]
    public void RecordEvent_OptedInRun_AppendsOneEvent()
    {
        TelemetryService.RecordEvent(_home, new Options { Analyze = true }, ExitCodes.Success, TimeSpan.FromSeconds(2));

        var payload = JsonDocument.Parse(ReadSingleEvent()).RootElement;
        payload.GetProperty("operation").GetString().Should().Be("analyze");
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        payload.GetProperty("exitCodeCategory").GetString().Should().Be("success");
        payload.GetProperty("durationMs").GetInt64().Should().Be(2000);
        payload.TryGetProperty("flags", out _).Should().BeTrue();
    }

    [Fact]
    public void RecordEvent_FailedRun_RecordsFailureCategory()
    {
        TelemetryService.RecordEvent(_home, new Options { UpdatePackages = true }, ExitCodes.TestFailure, TimeSpan.Zero);

        var payload = JsonDocument.Parse(ReadSingleEvent()).RootElement;
        payload.GetProperty("operation").GetString().Should().Be("update-packages");
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("exitCodeCategory").GetString().Should().Be("test-failure");
    }

    [Fact]
    public void RecordEvent_PayloadContainsNoRepositoryContent()
    {
        // The privacy contract: operational facts only. Package IDs, paths, and versions
        // must never appear in the event.
        TelemetryService.RecordEvent(
            _home,
            new Options { Analyze = true, SolutionFileDir = "/secret/acme" },
            ExitCodes.Success,
            TimeSpan.FromSeconds(1));

        var line = ReadSingleEvent();
        line.Should().NotContain("acme");
        line.Should().NotContain("/secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RecordEvent_UnresolvableHome_WritesNothingAndThrowsNothing(string? home)
    {
        // Containers and service accounts can have no resolvable home. That must be silence,
        // not a .cpmigrate directory scattered into whatever the working directory is.
        var cwdMarker = Path.Combine(Directory.GetCurrentDirectory(), ".cpmigrate");
        var existed = Directory.Exists(cwdMarker);

        var act = () => TelemetryService.RecordEvent(home, new Options(), ExitCodes.Success, TimeSpan.FromSeconds(1));

        act.Should().NotThrow();
        Directory.Exists(cwdMarker).Should().Be(existed, "telemetry must never scatter into the working directory");
    }

    [Theory]
    [InlineData(nameof(Options.Doctor), "doctor")]
    [InlineData(nameof(Options.Init), "init")]
    [InlineData(nameof(Options.Status), "status")]
    [InlineData(nameof(Options.Tree), "tree")]
    [InlineData(nameof(Options.Remediate), "remediate")]
    [InlineData(nameof(Options.UnifyProps), "unify-props")]
    [InlineData(nameof(Options.Update), "update")]
    [InlineData(nameof(Options.ListBackups), "list-backups")]
    [InlineData(nameof(Options.PruneBackups), "prune-backups")]
    [InlineData(nameof(Options.PruneAll), "prune-all")]
    public void RecordEvent_RecordsTheCommandThatRan(string flag, string operation)
    {
        // The operation label is the one fact this recorder exists to get right: a --doctor run
        // recorded as "migrate" is not close enough — it makes the data wrong.
        var options = new Options();
        typeof(Options).GetProperty(flag)!.SetValue(options, true);

        TelemetryService.RecordEvent(_home, options, ExitCodes.Success, TimeSpan.Zero);

        JsonDocument.Parse(ReadSingleEvent()).RootElement
            .GetProperty("operation").GetString().Should().Be(operation);
    }

    [Fact]
    public void RecordEvent_Why_RecordsWhy()
    {
        TelemetryService.RecordEvent(_home, new Options { Why = "Serilog" }, ExitCodes.Success, TimeSpan.Zero);

        JsonDocument.Parse(ReadSingleEvent()).RootElement
            .GetProperty("operation").GetString().Should().Be("why");
    }

    [Fact]
    public void RecordEvent_Completions_RecordsCompletions()
    {
        TelemetryService.RecordEvent(_home, new Options { Completions = CompletionShell.Bash }, ExitCodes.Success, TimeSpan.Zero);

        JsonDocument.Parse(ReadSingleEvent()).RootElement
            .GetProperty("operation").GetString().Should().Be("completions");
    }

    [Fact]
    public void RecordEvent_DefaultOptions_RecordsMigrate()
    {
        TelemetryService.RecordEvent(_home, new Options(), ExitCodes.Success, TimeSpan.Zero);

        JsonDocument.Parse(ReadSingleEvent()).RootElement
            .GetProperty("operation").GetString().Should().Be("migrate");
    }

    private string ReadSingleEvent()
    {
        var file = Path.Combine(_home, ".cpmigrate", "telemetry", "events.ndjson");
        File.Exists(file).Should().BeTrue("the event must be recorded");
        var lines = File.ReadAllLines(file);
        lines.Should().HaveCount(1);
        return lines[0];
    }
}
