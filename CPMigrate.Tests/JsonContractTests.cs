using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Spectre.Console;
using System.Text.Json;

namespace CPMigrate.Tests;

/// <summary>
/// Strict JSON contract integration tests: under <c>--output Json --quiet</c>, stdout must contain
/// only machine-readable JSON (no banners, "Found project:" notices, or color escapes). These guard
/// the CI parser contract documented in README.md.
/// </summary>
[Collection("Sequential")]
public class JsonContractTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionResolver _versionResolver;

    public JsonContractTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateJsonContract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _versionResolver = new VersionResolver(SilentConsoleService.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_JsonQuiet_StdoutIsPureJson()
    {
        CreateFixture();
        var stdout = await CaptureStdoutAsync(() =>
            CommandRouter.RouteCommand(
                new Options
                {
                    Analyze = true,
                    IncludeTransitive = true,
                    Output = OutputFormat.Json,
                    Quiet = true,
                    SolutionFileDir = _testDirectory,
                },
                new SpectreConsoleService(_versionResolver),
                new InteractiveService(SilentConsoleService.Instance),
                _versionResolver,
                new ConfigService(SilentConsoleService.Instance),
                new BackupManager()));

        stdout.Should().StartWith("{", "JSON mode must emit no preamble before the opening brace");
        var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("operation").GetString().Should().Be("analyze");
    }

    [Fact]
    public async Task MigrateDryRun_JsonQuiet_StdoutIsPureJson()
    {
        CreateFixture();
        var stdout = await CaptureStdoutAsync(() =>
            CommandRouter.RouteCommand(
                new Options
                {
                    DryRun = true,
                    Output = OutputFormat.Json,
                    Quiet = true,
                    SolutionFileDir = _testDirectory,
                    NoBackup = true,
                },
                new SpectreConsoleService(_versionResolver),
                new InteractiveService(SilentConsoleService.Instance),
                _versionResolver,
                new ConfigService(SilentConsoleService.Instance),
                new BackupManager()));

        stdout.Should().StartWith("{", "JSON mode must emit no preamble before the opening brace");
        var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("operation").GetString().Should().Be("migrate");
    }

    [Fact]
    public async Task WhyMany_JsonQuiet_StdoutIsOneWhyManyDocument()
    {
        CreateFixture();
        var stdout = await CaptureStdoutAsync(() =>
            ProgramRunner.RunAsync(
                [
                    "--why", "Newtonsoft.Json,Missing.Package",
                    "--output", "Json",
                    "--quiet",
                    "-s", _testDirectory,
                ],
                new TestDoubles.FakeConsoleService()));

        stdout.Should().StartWith("{", "JSON mode must emit no preamble before the opening brace");
        var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        root.GetProperty("operation").GetString().Should().Be("why-many");
        root.GetProperty("packageIds").EnumerateArray()
            .Select(id => id.GetString())
            .Should()
            .Equal("Newtonsoft.Json", "Missing.Package");

        var results = root.GetProperty("results").EnumerateArray().ToList();
        results.Should().HaveCount(2);
        results.Select(r => r.GetProperty("packageId").GetString())
            .Should()
            .Equal("Newtonsoft.Json", "Missing.Package");
        // The declared package is found over a fully-read workspace (0); the invented one is
        // genuinely absent (1) — so the process, and the mirrored document field, exit 1.
        results[0].GetProperty("status").GetString().Should().Be("found");
        results[0].GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        results[1].GetProperty("status").GetString().Should().Be("not-found");
        results[1].GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.ValidationError);
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.ValidationError);
    }

    [Fact]
    public async Task WhySingle_JsonQuiet_StdoutIsStillTheWhyReportShape()
    {
        CreateFixture();
        var stdout = await CaptureStdoutAsync(() =>
            ProgramRunner.RunAsync(
                [
                    "--why", "Newtonsoft.Json",
                    "--output", "Json",
                    "--quiet",
                    "-s", _testDirectory,
                ],
                new TestDoubles.FakeConsoleService()));

        var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        root.GetProperty("operation").GetString().Should().Be("why");
        root.GetProperty("packageId").GetString().Should().Be("Newtonsoft.Json");
        root.GetProperty("status").GetString().Should().Be("found");
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.TryGetProperty("results", out _).Should()
            .BeFalse("the single-package document must not grow a results array");
    }

    private void CreateFixture()
    {
        // Two projects with the same package at different versions — produces an
        // inconsistency that the analyzer will surface (and SolutionDiscovery will
        // emit "Found project:" notices for if the silent console isn't wired in).
        CreateProject("Api.csproj", "13.0.1");
        CreateProject("Lib.csproj", "12.0.3");
        CreateSolution("Test.sln", "Api.csproj", "Lib.csproj");
    }

    private void CreateProject(string name, string version)
    {
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllText(path, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""{version}"" />
  </ItemGroup>
</Project>");
    }

    private void CreateSolution(string name, params string[] projectNames)
    {
        var path = Path.Combine(_testDirectory, name);
        var content = "Microsoft Visual Studio Solution File, Format Version 12.00\n";
        foreach (var p in projectNames)
        {
            var guid = Guid.NewGuid().ToString("B").ToUpper();
            var projName = Path.GetFileNameWithoutExtension(p);
            content += $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projName}"", ""{p}"", ""{guid}""
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