using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Spectre.Console;

namespace CPMigrate.Tests;

/// <summary>
/// End-to-end contract for <c>--output Csv</c>: stdout must be the CSV document and nothing else,
/// and the document must come from <see cref="CsvFormatter"/> — the renderer the unit contract pins —
/// rather than a second escaper that can drift from it (the production path once shipped a bare
/// carriage return unquoted for exactly that reason).
/// </summary>
[Collection("Sequential")]
public class CsvOutputContractTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionResolver _versionResolver;

    public CsvOutputContractTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateCsv_{Guid.NewGuid():N}");
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
    public async Task Analyze_Csv_StdoutIsTheDocumentAndNothingElse()
    {
        CreateFixture();

        var stdout = await CaptureStdoutAsync(() => RunAnalyzeAsync(o => { }));

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0]
            .TrimEnd('\r')
            .Should()
            .Be(
                "Rule,Severity,Package,Description,AffectedProjects,Fixable",
                "the document is redirected verbatim into a spreadsheet"
            );
        stdout.Should().Contain("Newtonsoft.Json");
        stdout.Should().Contain("Version Inconsistencies");
        stdout.Should().NotContain("Found project:", "no discovery notices may leak in");
    }

    [Fact]
    public async Task Analyze_CsvToAFile_WritesTheDocumentAndKeepsStdoutClean()
    {
        CreateFixture();
        var outputFile = Path.Combine(_testDirectory, "report.csv");

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync(o => o.OutputFile = outputFile)
        );

        File.Exists(outputFile).Should().BeTrue();
        (await File.ReadAllTextAsync(outputFile))
            .Should()
            .Contain("Rule,Severity,Package,Description,AffectedProjects,Fixable");
        stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_CsvWithoutAnalyze_IsRejected()
    {
        // A migration has no analyzer findings, so the old behaviour emitted a header-only file a
        // CI consumer would read as "no findings" from a run that never looked.
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--output", "Csv", "-s", _testDirectory },
            console
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        console.ErrorMessages.Should().Contain(m => m.Contains("Csv"));
    }

    [Theory]
    [InlineData("--update")]
    [InlineData("--unify-props")]
    [InlineData("--rollback")]
    public async Task RunAsync_CsvWithAModeThatRunsInsteadOfAnalysis_IsRejected(string mode)
    {
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { mode, "--analyze", "--output", "Csv", "-s", _testDirectory, "--force" },
            console
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        console.ErrorMessages.Should().Contain(m => m.Contains("Csv"));
    }

    [Fact]
    public void Validate_CsvWithBatch_IsRejected()
    {
        // Batch aggregates into a BatchResult this report has no shape for, so the command would
        // emit nothing at all — worse than refusing.
        var options = new Options
        {
            Analyze = true,
            Output = OutputFormat.Csv,
            BatchDir = _testDirectory,
        };

        var validate = () => options.ValidateReportingContract();

        validate.Should().Throw<ArgumentException>().WithMessage("*--batch*");
    }

    private Task<int> RunAnalyzeAsync(Action<Options> configure)
    {
        var options = new Options
        {
            Analyze = true,
            Output = OutputFormat.Csv,
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
