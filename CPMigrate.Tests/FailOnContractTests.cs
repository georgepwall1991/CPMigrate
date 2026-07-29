using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Spectre.Console;

namespace CPMigrate.Tests;

/// <summary>
/// End-to-end contract for <c>--fail-on</c>: the exit code follows the threshold, and the JSON
/// payload carries enough context for a consumer to tell a clean run from a gated one without
/// re-deriving the policy itself.
/// </summary>
[Collection("Sequential")]
public class FailOnContractTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionResolver _versionResolver;

    public FailOnContractTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateFailOn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _versionResolver = new VersionResolver(SilentConsoleService.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Analyze_VersionInconsistencyWithHighThreshold_ExitsSuccess()
    {
        // A version inconsistency is Moderate, so a High gate must let it through.
        CreateFixture();

        var exitCode = await RunAnalyzeAsync(FailOnSeverity.High);

        exitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task Analyze_VersionInconsistencyWithModerateThreshold_Fails()
    {
        CreateFixture();

        var exitCode = await RunAnalyzeAsync(FailOnSeverity.Moderate);

        exitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task Analyze_DefaultThreshold_StillFailsOnAnyFinding()
    {
        CreateFixture();

        var exitCode = await RunAnalyzeAsync(null);

        exitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task Analyze_JsonCarriesTheThresholdAndTheGatedCount()
    {
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(FailOnSeverity.High, OutputFormat.Json)
        );

        var summary = JsonDocument.Parse(stdout).RootElement.GetProperty("summary");
        summary.GetProperty("failOnSeverity").GetString().Should().Be("High");
        summary.GetProperty("issuesFound").GetInt32().Should().BeGreaterThan(0);
        summary
            .GetProperty("issuesAtOrAboveThreshold")
            .GetInt32()
            .Should()
            .Be(0, "the findings are Moderate, below the High gate");
        summary.GetProperty("highestSeverity").GetString().Should().Be("Moderate");
        summary.GetProperty("scanFailures").GetInt32().Should().Be(0);
        summary.GetProperty("deepScanFailures").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Analyze_JsonReportsSuccessWhenFindingsAreBelowTheGate()
    {
        // `success: true` with a non-empty analysisIssues list is the whole point: findings are
        // reported, the build is not failed, and the threshold explains why.
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(FailOnSeverity.Never, OutputFormat.Json)
        );

        var root = JsonDocument.Parse(stdout).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("analysisIssues").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("outputSchemaVersion")
            .GetString()
            .Should()
            .Be(OutputMetadata.SchemaVersion);
    }

    [Fact]
    public async Task Analyze_WithSuccessfulFix_ReportsAGatedCountConsistentWithTheExitCode()
    {
        // issuesAtOrAboveThreshold exists to explain the exit code. A successful --fix leaves the
        // report describing the tree as it was *before* the fixes, and the run deliberately does
        // not gate on findings it just repaired — so reporting a positive count next to exitCode 0
        // would contradict the field's whole purpose.
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() =>
            CommandRouter.RouteCommand(
                new Options
                {
                    Analyze = true,
                    Fix = true,
                    Output = OutputFormat.Json,
                    Quiet = true,
                    SolutionFileDir = _testDirectory,
                    NoBackup = true,
                },
                new SpectreConsoleService(_versionResolver),
                new InteractiveService(SilentConsoleService.Instance),
                _versionResolver,
                new ConfigService(SilentConsoleService.Instance),
                new BackupManager()
            )
        );

        var root = JsonDocument.Parse(stdout).RootElement;
        var exitCode = root.GetProperty("exitCode").GetInt32();
        var gated = root.GetProperty("summary").GetProperty("issuesAtOrAboveThreshold").GetInt32();

        if (exitCode == ExitCodes.Success)
        {
            gated.Should().Be(0, "a successful run must not claim findings reached the gate");
        }
        else
        {
            gated
                .Should()
                .BeGreaterThan(0, "a gated failure must name how many findings caused it");
        }
    }

    [Fact]
    public async Task RunAsync_FailOnWithoutAnalyze_WarnsRatherThanSilentlyMigrating()
    {
        // --fail-on only affects analysis exit codes, so passing it without --analyze means the
        // default action — a real, file-rewriting migration — runs with the flag doing nothing.
        // A warning is the right level: rejecting would break anyone who sets failOn in config
        // and also runs migrations.
        CreateFixture();
        var console = new TestDoubles.FakeConsoleService();

        await ProgramRunner.RunAsync(
            new[] { "--fail-on", "High", "--dry-run", "--quiet", "-s", _testDirectory },
            console
        );

        console
            .OutputMessages.Should()
            .Contain(m => m.Contains("--fail-on") && m.Contains("--analyze"));
    }

    [Fact]
    public async Task RunAsync_FailOnWithAnalyze_DoesNotWarn()
    {
        CreateFixture();
        var console = new TestDoubles.FakeConsoleService();

        await ProgramRunner.RunAsync(
            new[] { "--analyze", "--fail-on", "High", "--quiet", "-s", _testDirectory },
            console
        );

        console.OutputMessages.Should().NotContain(m => m.Contains("--fail-on"));
    }

    private Task<int> RunAnalyzeAsync(
        FailOnSeverity? failOn,
        OutputFormat format = OutputFormat.Terminal
    )
    {
        var options = new Options
        {
            Analyze = true,
            Output = format,
            Quiet = true,
            SolutionFileDir = _testDirectory,
        };

        if (failOn.HasValue)
        {
            options.FailOn = failOn.Value;
        }

        return CommandRouter.RouteCommand(
            options,
            new SpectreConsoleService(_versionResolver),
            new InteractiveService(SilentConsoleService.Instance),
            _versionResolver,
            new ConfigService(SilentConsoleService.Instance),
            new BackupManager()
        );
    }

    private void CreateFixture()
    {
        CreateProject("Api.csproj", "13.0.1");
        CreateProject("Lib.csproj", "12.0.3");
        CreateSolution("Test.sln", "Api.csproj", "Lib.csproj");
    }

    private void CreateProject(string name, string version)
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, name),
            $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""{version}"" />
  </ItemGroup>
</Project>"
        );
    }

    private void CreateSolution(string name, params string[] projectNames)
    {
        var content = "Microsoft Visual Studio Solution File, Format Version 12.00\n";
        foreach (var projectFile in projectNames)
        {
            var guid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            content +=
                $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{projectFile}"", ""{guid}""
EndProject
";
        }

        File.WriteAllText(Path.Combine(_testDirectory, name), content);
    }

    private static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings());
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings());
        }

        return writer.ToString().Trim();
    }
}
