using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Spectre.Console;

namespace CPMigrate.Tests;

/// <summary>
/// End-to-end contract for <c>--output Sarif</c>: stdout must be a SARIF log and nothing else,
/// findings must survive the round trip, and the format must be rejected for commands that
/// produce no analyzer findings.
/// </summary>
[Collection("Sequential")]
public class SarifOutputContractTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionResolver _versionResolver;

    public SarifOutputContractTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateSarifContract_{Guid.NewGuid():N}"
        );
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
    public async Task Analyze_SarifQuiet_StdoutIsPureSarif()
    {
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() => RunAnalyzeAsync(OutputFormat.Sarif));

        stdout.Should().StartWith("{", "SARIF mode must emit no preamble before the opening brace");
        var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("version").GetString().Should().Be("2.1.0");
        doc.RootElement.GetProperty("runs")[0]
            .GetProperty("tool")
            .GetProperty("driver")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("CPMigrate");
    }

    [Fact]
    public async Task Analyze_SarifQuiet_ReportsTheVersionInconsistencyWithAProjectLocation()
    {
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() => RunAnalyzeAsync(OutputFormat.Sarif));

        var results = JsonDocument
            .Parse(stdout)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results");
        var inconsistency = results
            .EnumerateArray()
            .Should()
            .ContainSingle(r => r.GetProperty("ruleId").GetString() == "VersionInconsistency")
            .Subject;

        inconsistency.GetProperty("level").GetString().Should().Be("warning");
        inconsistency
            .GetProperty("message")
            .GetProperty("text")
            .GetString()
            .Should()
            .Contain("Newtonsoft.Json");

        var uris = inconsistency
            .GetProperty("locations")
            .EnumerateArray()
            .Select(l =>
                l.GetProperty("physicalLocation")
                    .GetProperty("artifactLocation")
                    .GetProperty("uri")
                    .GetString()
            )
            .ToList();
        uris.Should().BeEquivalentTo(new[] { "Api.csproj", "Lib.csproj" });
    }

    [Fact]
    public async Task Analyze_SarifWithOutputFile_WritesSarifToDiskAndKeepsStdoutClean()
    {
        CreateFixture();
        var outputFile = Path.Combine(_testDirectory, "results.sarif");

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(OutputFormat.Sarif, outputFile)
        );

        File.Exists(outputFile).Should().BeTrue();
        JsonDocument
            .Parse(await File.ReadAllTextAsync(outputFile))
            .RootElement.GetProperty("version")
            .GetString()
            .Should()
            .Be("2.1.0");
        stdout
            .Should()
            .BeEmpty("the SARIF log went to the file, and --quiet suppresses the notice");
    }

    [Fact]
    public async Task Analyze_SarifWithNoProjectsFound_ReportsAnUnsuccessfulInvocation()
    {
        // No solution and no projects: the scan never ran, so an empty result set must not be
        // presented to code scanning as a clean bill of health.
        var empty = Path.Combine(_testDirectory, "empty");
        Directory.CreateDirectory(empty);

        var stdout = await CaptureStdoutAsync(() =>
            CommandRouter.RouteCommand(
                new Options
                {
                    Analyze = true,
                    Output = OutputFormat.Sarif,
                    Quiet = true,
                    SolutionFileDir = empty,
                },
                new SpectreConsoleService(_versionResolver),
                new InteractiveService(SilentConsoleService.Instance),
                _versionResolver,
                new ConfigService(SilentConsoleService.Instance),
                new BackupManager()
            )
        );

        var invocation = JsonDocument
            .Parse(stdout)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0];

        invocation.GetProperty("executionSuccessful").GetBoolean().Should().BeFalse();
        invocation
            .GetProperty("toolExecutionNotifications")
            .GetArrayLength()
            .Should()
            .BeGreaterThan(0);
    }

    [Fact]
    public async Task Analyze_SarifWithUnwritableOutputFile_FallsBackToStdoutInsteadOfCrashing()
    {
        CreateFixture();
        var unwritable = Path.Combine(_testDirectory, "does", "not", "exist", "out.sarif");

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(OutputFormat.Sarif, unwritable)
        );

        // The write fails, but the run must still report through stdout rather than aborting
        // with an unhandled exception while trying to write the failure to the same bad path.
        stdout.Should().StartWith("{");
        JsonDocument
            .Parse(stdout)
            .RootElement.GetProperty("version")
            .GetString()
            .Should()
            .Be("2.1.0");
    }

    [Fact]
    public void Validate_SarifWithoutAnalyze_IsRejected()
    {
        var options = new Options { Output = OutputFormat.Sarif, SolutionFileDir = _testDirectory };

        var validate = () => options.Validate();

        validate.Should().Throw<ArgumentException>().WithMessage("*--output Sarif*--analyze*");
    }

    [Fact]
    public void Validate_SarifWithAnalyze_IsAccepted()
    {
        CreateFixture();
        var options = new Options
        {
            Analyze = true,
            Output = OutputFormat.Sarif,
            OutputFile = Path.Combine(_testDirectory, "results.sarif"),
            SolutionFileDir = _testDirectory,
        };

        var validate = () => options.Validate();

        validate.Should().NotThrow();
    }

    [Fact]
    public void Validate_SarifWithInteractive_IsRejected()
    {
        var options = new Options
        {
            Analyze = true,
            Interactive = true,
            Output = OutputFormat.Sarif,
            SolutionFileDir = _testDirectory,
        };

        var validate = () => options.Validate();

        validate.Should().Throw<ArgumentException>();
    }

    private Task<int> RunAnalyzeAsync(OutputFormat format, string? outputFile = null)
    {
        return CommandRouter.RouteCommand(
            new Options
            {
                Analyze = true,
                Output = format,
                OutputFile = outputFile,
                Quiet = true,
                SolutionFileDir = _testDirectory,
            },
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
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllText(
            path,
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
        var path = Path.Combine(_testDirectory, name);
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

        File.WriteAllText(path, content);
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
