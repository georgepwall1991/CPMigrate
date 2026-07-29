using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Spectre.Console;

namespace CPMigrate.Tests;

/// <summary>
/// End-to-end contract for baseline suppression — the adoption path for a repository that already
/// has debt: record what exists, then fail only on what is new.
/// </summary>
[Collection("Sequential")]
public class BaselineContractTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionResolver _versionResolver;

    public BaselineContractTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateBaselineContract_{Guid.NewGuid():N}"
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
    public async Task WriteBaseline_RecordsCurrentFindingsAndExitsSuccess()
    {
        CreateFixture();
        var baselinePath = Path.Combine(_testDirectory, "baseline.json");

        var exitCode = await RunAsync(o =>
        {
            o.WriteBaseline = true;
            o.Baseline = baselinePath;
        });

        exitCode.Should().Be(ExitCodes.Success, "recording a baseline is not a failure");
        File.Exists(baselinePath).Should().BeTrue();

        var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath)).RootElement;
        baseline.GetProperty("findings").GetArrayLength().Should().BeGreaterThan(0);
        baseline
            .GetProperty("fingerprintVersion")
            .GetString()
            .Should()
            .Be(AnalysisIssueIdentity.Version);
    }

    [Fact]
    public async Task Analyze_WithBaselineCoveringEverything_ExitsSuccess()
    {
        CreateFixture();
        var baselinePath = Path.Combine(_testDirectory, "baseline.json");
        await RunAsync(o =>
        {
            o.WriteBaseline = true;
            o.Baseline = baselinePath;
        });

        var exitCode = await RunAsync(o => o.Baseline = baselinePath);

        exitCode.Should().Be(ExitCodes.Success, "every finding was accepted");
    }

    [Fact]
    public async Task Analyze_WithoutBaseline_StillFailsOnTheSameFindings()
    {
        CreateFixture();

        var exitCode = await RunAsync(_ => { });

        exitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task Analyze_NewFindingOutsideTheBaseline_Fails()
    {
        CreateFixture();
        var baselinePath = Path.Combine(_testDirectory, "baseline.json");
        await RunAsync(o =>
        {
            o.WriteBaseline = true;
            o.Baseline = baselinePath;
        });

        // A second package drifts: debt the baseline never accepted.
        CreateProject("Extra.csproj", "Serilog", "4.0.0");
        CreateProject("Extra2.csproj", "Serilog", "3.0.0");
        CreateSolution("Test.sln", "Api.csproj", "Lib.csproj", "Extra.csproj", "Extra2.csproj");

        var exitCode = await RunAsync(o => o.Baseline = baselinePath);

        exitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task Analyze_BaselinedFindingsAreStillReported()
    {
        // Accepting debt must not hide it: the finding stays in the payload, flagged.
        CreateFixture();
        var baselinePath = Path.Combine(_testDirectory, "baseline.json");
        await RunAsync(o =>
        {
            o.WriteBaseline = true;
            o.Baseline = baselinePath;
        });

        var stdout = await CaptureStdoutAsync(() =>
            RunAsync(o =>
            {
                o.Baseline = baselinePath;
                o.Output = OutputFormat.Json;
            })
        );

        var root = JsonDocument.Parse(stdout).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("summary")
            .GetProperty("issuesBaselined")
            .GetInt32()
            .Should()
            .BeGreaterThan(0);

        var issues = root.GetProperty("analysisIssues").EnumerateArray().ToList();
        issues.Should().NotBeEmpty("suppressed findings remain visible");
        issues.Should().Contain(i => i.GetProperty("suppressed").GetBoolean());
    }

    [Fact]
    public async Task Analyze_SarifMarksBaselinedFindingsAsSuppressed()
    {
        CreateFixture();
        var baselinePath = Path.Combine(_testDirectory, "baseline.json");
        await RunAsync(o =>
        {
            o.WriteBaseline = true;
            o.Baseline = baselinePath;
        });

        var stdout = await CaptureStdoutAsync(() =>
            RunAsync(o =>
            {
                o.Baseline = baselinePath;
                o.Output = OutputFormat.Sarif;
            })
        );

        var results = JsonDocument
            .Parse(stdout)
            .RootElement.GetProperty("runs")[0]
            .GetProperty("results")
            .EnumerateArray()
            .ToList();

        results.Should().NotBeEmpty();
        var suppressed = results.Where(r => r.TryGetProperty("suppressions", out var _)).ToList();
        suppressed.Should().NotBeEmpty("the baselined finding must carry a SARIF suppression");
        suppressed[0]
            .GetProperty("suppressions")[0]
            .GetProperty("kind")
            .GetString()
            .Should()
            .Be("external", "the suppression came from a baseline file, not from source");
    }

    [Fact]
    public async Task Analyze_MissingBaselineFile_IsAValidationError()
    {
        // Failing loudly beats silently gating on everything: a mistyped path would otherwise look
        // like a repository whose debt was never accepted.
        CreateFixture();

        var exitCode = await RunAsync(o =>
            o.Baseline = Path.Combine(_testDirectory, "absent.json")
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
    }

    [Fact]
    public async Task Analyze_BaselineWithFix_KeepsSuppressingAcceptedFindingsAfterTheRescan()
    {
        // --fix triggers a rescan, which produces a fresh unsuppressed report. If the baseline is
        // not reapplied to it, accepted debt starts failing the build the moment an unrelated fix
        // runs — the exact opposite of what a baseline is for.
        CreateFixture();

        // A deprecated-style finding no fixer addresses, so something survives the fix.
        CreateProject("Extra.csproj", "Serilog", "4.0.0");
        CreateProject("Extra2.csproj", "Serilog", "3.0.0");
        CreateSolution("Test.sln", "Api.csproj", "Lib.csproj", "Extra.csproj", "Extra2.csproj");

        var baselinePath = Path.Combine(_testDirectory, "baseline.json");
        await RunAsync(o =>
        {
            o.WriteBaseline = true;
            o.Baseline = baselinePath;
        });

        var exitCode = await RunAsync(o =>
        {
            o.Baseline = baselinePath;
            o.Fix = true;
            o.NoBackup = true;
        });

        exitCode
            .Should()
            .NotBe(
                ExitCodes.AnalysisIssuesFound,
                "every finding was accepted in the baseline, before and after the fixes"
            );
    }

    [Fact]
    public async Task WriteBaseline_WithAnIncompleteScan_RefusesToRecordAndReportsIncomplete()
    {
        // A baseline claims to be the accepted current state. Recording one from a partial scan
        // permanently accepts findings that were never looked for.
        CreateProject("Api.csproj", "Newtonsoft.Json", "13.0.1");
        File.WriteAllText(Path.Combine(_testDirectory, "Broken.csproj"), "<Project><ItemGroup>");
        CreateSolution("Test.sln", "Api.csproj", "Broken.csproj");

        var baselinePath = Path.Combine(_testDirectory, "baseline.json");
        var exitCode = await RunAsync(o =>
        {
            o.WriteBaseline = true;
            o.Baseline = baselinePath;
        });

        exitCode.Should().Be(ExitCodes.IncompleteAnalysis);
        File.Exists(baselinePath).Should().BeFalse("a partial baseline is worse than none");
    }

    [Fact]
    public void Validate_WriteBaselineWithBatch_IsRejected()
    {
        // Each solution would write the same file: sequentially the last wins, in parallel they
        // race. Either way the baseline covers one solution while claiming to cover the repository.
        var options = new Options
        {
            Analyze = true,
            WriteBaseline = true,
            BatchDir = _testDirectory,
        };

        var validate = () => options.Validate();

        validate.Should().Throw<ArgumentException>().WithMessage("*--batch*");
    }

    [Fact]
    public void Validate_BaselineWithoutAnalyze_IsRejected()
    {
        var options = new Options { Baseline = "b.json", SolutionFileDir = _testDirectory };

        var validate = () => options.Validate();

        validate.Should().Throw<ArgumentException>().WithMessage("*--analyze*");
    }

    [Fact]
    public void Validate_WriteBaselineWithFix_IsRejected()
    {
        var options = new Options
        {
            Analyze = true,
            Fix = true,
            WriteBaseline = true,
            SolutionFileDir = _testDirectory,
        };

        var validate = () => options.Validate();

        validate.Should().Throw<ArgumentException>().WithMessage("*--fix*");
    }

    [Fact]
    public void ResolveBaselinePath_WithoutAValue_UsesTheRepositoryDefault()
    {
        new Options { WriteBaseline = true }
            .ResolveBaselinePath()
            .Should()
            .Be(Options.BaselineDefaultFileName);
    }

    private Task<int> RunAsync(Action<Options> configure)
    {
        var options = new Options
        {
            Analyze = true,
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
        CreateProject("Api.csproj", "Newtonsoft.Json", "13.0.1");
        CreateProject("Lib.csproj", "Newtonsoft.Json", "12.0.3");
        CreateSolution("Test.sln", "Api.csproj", "Lib.csproj");
    }

    private void CreateProject(string name, string package, string version)
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, name),
            $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""{package}"" Version=""{version}"" />
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
