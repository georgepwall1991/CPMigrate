using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Spectre.Console;

namespace CPMigrate.Tests;

/// <summary>
/// End-to-end contract for <c>--output Markdown</c>: stdout must be the report and nothing else, so
/// it can be redirected straight into a job summary or a PR comment.
/// </summary>
[Collection("Sequential")]
public class MarkdownOutputContractTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionResolver _versionResolver;

    public MarkdownOutputContractTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateMarkdown_{Guid.NewGuid():N}");
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
    public async Task Analyze_MarkdownQuiet_StdoutIsTheReportAndNothingElse()
    {
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() => RunAnalyzeAsync(o => { }));

        stdout.Should().StartWith("## ", "the report is redirected verbatim into a job summary");
        stdout.Should().Contain("CPMigrate — dependency analysis");
        stdout.Should().Contain("Newtonsoft.Json");
        stdout.Should().NotContain("Found project:", "no discovery notices may leak in");
        stdout.Should().NotContain("[", "no colour escapes may leak in");
    }

    [Fact]
    public async Task Analyze_MarkdownWithHighThreshold_ReportsPassingVerdict()
    {
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(o => o.FailOn = FailOnSeverity.High)
        );

        stdout.Should().StartWith("## ✅");
        stdout.Should().Contain("No findings reached the failure threshold");
        stdout.Should().Contain("Newtonsoft.Json", "the finding is reported even though it passed");
    }

    [Fact]
    public async Task Analyze_MarkdownWithLowThreshold_ReportsFailingVerdict()
    {
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(o => o.FailOn = FailOnSeverity.Moderate)
        );

        stdout.Should().StartWith("## ❌");
        stdout.Should().Contain("at or above **Moderate**");
    }

    [Fact]
    public async Task Analyze_MarkdownToAFile_WritesTheReportAndKeepsStdoutClean()
    {
        CreateFixture();
        var outputFile = Path.Combine(_testDirectory, "report.md");

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(o => o.OutputFile = outputFile)
        );

        File.Exists(outputFile).Should().BeTrue();
        (await File.ReadAllTextAsync(outputFile)).Should().StartWith("## ");
        stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task Analyze_MarkdownWithNoProjects_ReportsTheFailureAsMarkdown()
    {
        // A failure must not emit raw error JSON into what is meant to be a rendered summary.
        var empty = Path.Combine(_testDirectory, "empty");
        Directory.CreateDirectory(empty);

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(o => o.SolutionFileDir = empty)
        );

        stdout.Should().NotContain("\"exitCode\"", "the payload must not be JSON");
        stdout.Should().StartWith("## ");
        stdout
            .Should()
            .NotStartWith(
                "## ✅",
                "an analysis that never ran must not be presented as a clean result"
            );
    }

    [Fact]
    public async Task RunAsync_MarkdownWithoutAnalyze_IsRejected()
    {
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--output", "Markdown", "-s", _testDirectory },
            console
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        console.ErrorMessages.Should().Contain(m => m.Contains("Markdown"));
    }

    [Theory]
    [InlineData("--update")]
    [InlineData("--unify-props")]
    public async Task RunAsync_MarkdownWithAModeThatRunsInsteadOfAnalysis_IsRejected(string mode)
    {
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { mode, "--analyze", "--output", "Markdown", "-s", _testDirectory, "--force" },
            console
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        console.ErrorMessages.Should().Contain(m => m.Contains("Markdown"));
    }

    [Fact]
    public void Validate_MarkdownWithBatch_IsRejected()
    {
        // Batch aggregates into a BatchResult this report has no shape for, so the command would
        // emit nothing at all — worse than refusing.
        var options = new Options
        {
            Analyze = true,
            Output = OutputFormat.Markdown,
            BatchDir = _testDirectory,
        };

        var validate = () => options.Validate();

        validate.Should().Throw<ArgumentException>().WithMessage("*--batch*");
    }

    private Task<int> RunAnalyzeAsync(Action<Options> configure)
    {
        var options = new Options
        {
            Analyze = true,
            Output = OutputFormat.Markdown,
            Quiet = true,
            SolutionFileDir = _testDirectory,
        };
        configure(options);

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
