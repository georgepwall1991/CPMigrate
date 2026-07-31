using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Spectre.Console;

namespace CPMigrate.Tests;

/// <summary>
/// Contract for <c>--rules</c>: a team decides which rules apply and how hard each one bites.
///
/// The failure mode this guards against is a policy that silently does nothing — a misspelled rule
/// ID quietly leaving a rule armed, or a disabled rule that still reaches the gate. Both look
/// exactly like a working configuration from the outside, which is why the parse is strict and the
/// applied policy is reported in the machine-readable payload rather than being invisible.
/// </summary>
public class RulePolicyParsingTests
{
    [Fact]
    public void Parse_DisableKeyword_DisablesTheRule()
    {
        var (policy, error) = RulePolicy.Parse(new[] { "OutdatedPackage=none" });

        error.Should().BeNull();
        policy!.DisabledRules.Should().Equal(AnalysisIssueCode.OutdatedPackage);
        policy.SeverityOverrides.Should().BeEmpty();
        policy.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Parse_SeverityValue_OverridesTheRuleSeverity()
    {
        var (policy, error) = RulePolicy.Parse(new[] { "VersionInconsistency=Critical" });

        error.Should().BeNull();
        policy!.DisabledRules.Should().BeEmpty();
        policy
            .SeverityOverrides.Should()
            .Contain(
                new KeyValuePair<AnalysisIssueCode, AnalysisSeverity>(
                    AnalysisIssueCode.VersionInconsistency,
                    AnalysisSeverity.Critical
                )
            );
    }

    [Fact]
    public void Parse_IsCaseInsensitiveOnBothSides()
    {
        var (policy, error) = RulePolicy.Parse(
            new[] { "outdatedpackage=NONE", "licenserisk=high" }
        );

        error.Should().BeNull();
        policy!.DisabledRules.Should().Equal(AnalysisIssueCode.OutdatedPackage);
        policy.SeverityOverrides[AnalysisIssueCode.LicenseRisk].Should().Be(AnalysisSeverity.High);
    }

    [Fact]
    public void Parse_UnknownRuleId_IsAnErrorNamingTheRuleAndHowToListThem()
    {
        // The whole point of the strict parse: a typo that silently left the rule armed would be
        // indistinguishable from a policy that works.
        var (policy, error) = RulePolicy.Parse(new[] { "OutdatedPackages=none" });

        policy.Should().BeNull();
        error.Should().Contain("OutdatedPackages").And.Contain("--explain all");
    }

    [Fact]
    public void Parse_UnknownSeverity_IsAnErrorListingTheAcceptedValues()
    {
        var (policy, error) = RulePolicy.Parse(new[] { "OutdatedPackage=Severe" });

        policy.Should().BeNull();
        error.Should().Contain("Severe").And.Contain("none");
    }

    [Fact]
    public void Parse_EntryWithoutASeverity_IsAnError()
    {
        var (policy, error) = RulePolicy.Parse(new[] { "OutdatedPackage" });

        policy.Should().BeNull();
        error.Should().Contain("OutdatedPackage").And.Contain("=");
    }

    [Fact]
    public void Parse_RepeatedRule_TakesTheLastValue()
    {
        var (policy, error) = RulePolicy.Parse(
            new[] { "OutdatedPackage=none", "OutdatedPackage=Critical" }
        );

        error.Should().BeNull();
        policy!.DisabledRules.Should().BeEmpty();
        policy
            .SeverityOverrides[AnalysisIssueCode.OutdatedPackage]
            .Should()
            .Be(AnalysisSeverity.Critical);
    }

    [Theory]
    [InlineData("1=none")]
    [InlineData("VersionInconsistency=4")]
    [InlineData("1=4")]
    public void Parse_NumericTokens_AreRejected(string entry)
    {
        // Enum.TryParse also accepts the underlying numbers, which would make '1=none' a synonym for
        // 'VersionInconsistency=none'. Neither form is documented or in the published schema, and
        // both would silently re-target the moment a member were inserted into either enum.
        var (policy, error) = RulePolicy.Parse(new[] { entry });

        policy.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Parse_NoEntries_IsTheEmptyPolicy()
    {
        var (policy, error) = RulePolicy.Parse(Array.Empty<string>());

        error.Should().BeNull();
        policy!.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("A=none,B=High", 2)]
    [InlineData(" A=none , B=High ", 2)]
    [InlineData("A=none,,", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void SplitSpec_SplitsOnCommasAndIgnoresEmptySegments(string? spec, int expected)
    {
        RulePolicy.SplitSpec(spec).Should().HaveCount(expected);
    }

    [Fact]
    public void Apply_DisabledRule_RemovesItsFindingsEntirely()
    {
        // Not "suppressed": a baseline accepts known debt and keeps it visible, while disabling a
        // rule is a statement that the finding is not wanted at all. Conflating the two would put
        // findings a team switched off back into every report.
        var (policy, _) = RulePolicy.Parse(new[] { "OutdatedPackage=none" });

        var applied = policy!.Apply(
            ReportWith(
                Issue(AnalysisIssueCode.OutdatedPackage),
                Issue(AnalysisIssueCode.LicenseRisk)
            )
        );

        applied
            .Results.SelectMany(result => result.Issues)
            .Select(issue => issue.IssueCode)
            .Should()
            .Equal(AnalysisIssueCode.LicenseRisk);
    }

    [Fact]
    public void Apply_SeverityOverride_RewritesTheSeverityAndLeavesEverythingElse()
    {
        var (policy, _) = RulePolicy.Parse(new[] { "LicenseRisk=Critical" });

        var applied = policy!.Apply(
            ReportWith(
                Issue(AnalysisIssueCode.LicenseRisk),
                Issue(AnalysisIssueCode.OutdatedPackage)
            )
        );

        var issues = applied.Results.SelectMany(result => result.Issues).ToList();
        issues
            .Single(issue => issue.IssueCode == AnalysisIssueCode.LicenseRisk)
            .Severity.Should()
            .Be(AnalysisSeverity.Critical);
        issues
            .Single(issue => issue.IssueCode == AnalysisIssueCode.OutdatedPackage)
            .Severity.Should()
            .Be(AnalysisSeverity.Low, "an untouched rule keeps the severity its analyzer assigned");
    }

    [Fact]
    public void Apply_EmptyPolicy_ReturnsTheReportUnchanged()
    {
        var report = ReportWith(Issue(AnalysisIssueCode.LicenseRisk));

        RulePolicy.Empty.Apply(report).Should().BeSameAs(report);
    }

    [Fact]
    public void Apply_KeepsAnalyzersThatLoseEveryFinding()
    {
        // An analyzer with no findings is a normal, reported outcome. Dropping the result entirely
        // would make a disabled rule look like an analyzer that never ran.
        var (policy, _) = RulePolicy.Parse(new[] { "LicenseRisk=none" });

        var applied = policy!.Apply(ReportWith(Issue(AnalysisIssueCode.LicenseRisk)));

        applied.Results.Should().HaveCount(1);
        applied.Results[0].Issues.Should().BeEmpty();
    }

    private static AnalysisIssue Issue(AnalysisIssueCode code) =>
        new(
            "Some.Package",
            $"finding for {code}",
            new[] { "src/App/App.csproj" },
            code,
            AnalysisSeverity.Low
        );

    private static AnalysisReport ReportWith(params AnalysisIssue[] issues) =>
        new(1, 1, new[] { new AnalyzerResult("Test Analyzer", issues) });
}

/// <summary>
/// End-to-end contract for the rule policy: what the CLI, the config file, and the JSON payload do
/// with it once a real analysis has produced findings.
/// </summary>
[Collection("Sequential")]
public class RulePolicyContractTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly VersionResolver _versionResolver;

    public RulePolicyContractTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateRules_{Guid.NewGuid():N}");
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
    public async Task Analyze_WithoutPolicy_ReportsTheVersionInconsistency()
    {
        CreateInconsistentFixture();

        var exitCode = await RunAnalyzeAsync(rules: null);

        exitCode.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task Analyze_WithTheRuleDisabled_FindsNothingAndExitsClean()
    {
        CreateInconsistentFixture();

        var exitCode = await RunAnalyzeAsync("VersionInconsistency=none");

        exitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task Analyze_DisabledRule_IsAbsentFromTheJsonFindings()
    {
        CreateInconsistentFixture();

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync("VersionInconsistency=none", OutputFormat.Json)
        );

        var root = JsonDocument.Parse(stdout).RootElement;
        root.GetProperty("analysisIssues")
            .EnumerateArray()
            .Should()
            .NotContain(issue =>
                issue.GetProperty("issueCode").GetString() == "VersionInconsistency"
            );
        root.GetProperty("summary").GetProperty("issuesFound").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Analyze_AppliedPolicy_IsReportedInTheJsonSummary()
    {
        // A consumer reading `issuesFound: 0` needs to be able to tell a clean solution from one
        // whose findings were configured away. Leaving that out is the same defect class as
        // reporting a failed lookup as "up to date".
        CreateInconsistentFixture();

        var stdout = await CaptureStdoutAsync(() =>
            RunAnalyzeAsync("VersionInconsistency=none,LicenseRisk=Critical", OutputFormat.Json)
        );

        var summary = JsonDocument.Parse(stdout).RootElement.GetProperty("summary");
        summary
            .GetProperty("disabledRules")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .Equal("VersionInconsistency");
        summary
            .GetProperty("severityOverrides")
            .GetProperty("LicenseRisk")
            .GetString()
            .Should()
            .Be("Critical");
    }

    [Fact]
    public async Task Analyze_NoPolicy_LeavesThePolicyFieldsOutOfTheSummary()
    {
        CreateInconsistentFixture();

        var stdout = await CaptureStdoutAsync(() => RunAnalyzeAsync(null, OutputFormat.Json));

        var summary = JsonDocument.Parse(stdout).RootElement.GetProperty("summary");
        summary.TryGetProperty("disabledRules", out _).Should().BeFalse();
        summary.TryGetProperty("severityOverrides", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Migrate_UnderJson_DoesNotClaimAPolicyItNeverApplied()
    {
        // A migration produces no analysis report, so --rules shapes nothing. Publishing the policy
        // there would claim rules were switched off in a report they did not touch — the same lie as
        // omitting it from one they did, pointed the other way.
        CreateInconsistentFixture();

        var stdout = await CaptureStdoutAsync(() =>
            CommandRouter.RouteCommand(
                new Options
                {
                    DryRun = true,
                    Output = OutputFormat.Json,
                    Quiet = true,
                    SolutionFileDir = _testDirectory,
                    Rules = "VersionInconsistency=none",
                },
                new SpectreConsoleService(_versionResolver),
                new InteractiveService(SilentConsoleService.Instance),
                _versionResolver,
                new ConfigService(SilentConsoleService.Instance),
                new BackupManager()
            )
        );

        var summary = JsonDocument.Parse(stdout).RootElement.GetProperty("summary");
        summary.TryGetProperty("disabledRules", out _).Should().BeFalse();
        summary.TryGetProperty("severityOverrides", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownRuleId_UnderJson_IsReportedAsAParseablePayload()
    {
        // A CI step under --output Json parses stdout. A rejection printed as prose there is a
        // parse failure rather than a reported one, so the consumer learns the run broke but not
        // why — and a rule ID is a string someone typed into a workflow file, which makes this the
        // rejection most likely to be hit in CI.
        //
        // Deliberately driven without a console double. An earlier version of this test passed one,
        // which captured the diagnostic instead of letting it reach stdout — so it went green while
        // the real binary printed prose in front of the opening brace and broke every parser.
        CreateInconsistentFixture();

        var stdout = await CaptureStdoutAsync(() =>
            ProgramRunner.RunAsync(
                new[]
                {
                    "--analyze",
                    "--rules",
                    "NoSuchRule=none",
                    "--output",
                    "Json",
                    "-s",
                    _testDirectory,
                }
            )
        );

        var root = JsonDocument.Parse(stdout).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.ValidationError);
        root.GetProperty("errors")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .Contain(message => message!.Contains("NoSuchRule"));
    }

    [Fact]
    public async Task UnknownRuleId_UnderSarif_StillProducesAValidSarifLog()
    {
        // SARIF has the same contract and a worse failure mode: an upload step reads stdout as a
        // log, so prose in front of it fails the upload rather than reporting the finding.
        CreateInconsistentFixture();

        var stdout = await CaptureStdoutAsync(() =>
            ProgramRunner.RunAsync(
                new[]
                {
                    "--analyze",
                    "--rules",
                    "NoSuchRule=none",
                    "--output",
                    "Sarif",
                    "-s",
                    _testDirectory,
                }
            )
        );

        var root = JsonDocument.Parse(stdout).RootElement;
        root.GetProperty("runs")[0]
            .GetProperty("invocations")[0]
            .GetProperty("executionSuccessful")
            .GetBoolean()
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task Batch_CarriesThePolicyIntoEachSolutionSummary()
    {
        // Per-solution runs are quiet, so the terminal notice never reaches a batch consumer. Without
        // the policy in the payload, a batch that configured its findings away is indistinguishable
        // from a repository of clean solutions.
        var solutionDirectory = Path.Combine(_testDirectory, "Sln");
        Directory.CreateDirectory(solutionDirectory);
        CreateProject(Path.Combine("Sln", "Api.csproj"), "13.0.1");
        CreateProject(Path.Combine("Sln", "Lib.csproj"), "12.0.3");
        CreateSolution(Path.Combine("Sln", "Test.sln"), "Api.csproj", "Lib.csproj");

        var stdout = await CaptureStdoutAsync(() =>
            ProgramRunner.RunAsync(
                new[]
                {
                    "--batch",
                    _testDirectory,
                    "--analyze",
                    "--output",
                    "Json",
                    "--rules",
                    "VersionInconsistency=none",
                },
                new TestDoubles.FakeConsoleService()
            )
        );

        var summary = JsonDocument
            .Parse(stdout)
            .RootElement.GetProperty("solutions")[0]
            .GetProperty("summary");
        summary
            .GetProperty("disabledRules")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .Equal("VersionInconsistency");
    }

    [Fact]
    public async Task SideEffectingMode_WithAnUnusablePolicy_IsRejectedBeforeItRuns()
    {
        // --unify-props is dispatched ahead of per-command validation and rewrites project files.
        // A policy rejected only under --analyze would let it run straight past the promised strict
        // rejection and modify the tree.
        CreateInconsistentFixture();
        var console = new TestDoubles.FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[]
            {
                "--unify-props",
                "--rules",
                "NoSuchRule=none",
                "--quiet",
                "-s",
                _testDirectory,
            },
            console
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        console.ErrorMessages.Should().Contain(message => message.Contains("NoSuchRule"));
    }

    [Fact]
    public async Task Analyze_SeverityOverride_MovesAFindingAcrossTheGate()
    {
        // A version inconsistency is Moderate, so a High gate lets it through — until the team
        // decides the rule matters more than that.
        CreateInconsistentFixture();

        var withoutOverride = await RunAnalyzeAsync(null, failOn: FailOnSeverity.High);
        var withOverride = await RunAnalyzeAsync(
            "VersionInconsistency=Critical",
            failOn: FailOnSeverity.High
        );

        withoutOverride.Should().Be(ExitCodes.Success);
        withOverride.Should().Be(ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task Analyze_SeverityOverrideDownwards_MovesAFindingBelowTheGate()
    {
        CreateInconsistentFixture();

        var exitCode = await RunAnalyzeAsync(
            "VersionInconsistency=Info",
            failOn: FailOnSeverity.Moderate
        );

        exitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task Analyze_UnknownRuleId_FailsValidationRatherThanScanning()
    {
        CreateInconsistentFixture();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--analyze", "--rules", "NoSuchRule=none", "--quiet", "-s", _testDirectory },
            new TestDoubles.FakeConsoleService()
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
    }

    [Fact]
    public async Task Analyze_UnknownRuleId_SaysWhichRuleAndHowToListThem()
    {
        CreateInconsistentFixture();
        var console = new TestDoubles.FakeConsoleService();

        await ProgramRunner.RunAsync(
            new[] { "--analyze", "--rules", "NoSuchRule=none", "--quiet", "-s", _testDirectory },
            console
        );

        console
            .ErrorMessages.Should()
            .Contain(message =>
                message.Contains("NoSuchRule") && message.Contains("--explain all")
            );
    }

    [Fact]
    public async Task Analyze_PolicyFromConfigFile_Applies()
    {
        CreateInconsistentFixture();
        await File.WriteAllTextAsync(
            Path.Combine(_testDirectory, ".cpmigrate.json"),
            """{ "rules": { "VersionInconsistency": "none" } }"""
        );

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--analyze", "--quiet", "-s", _testDirectory },
            new TestDoubles.FakeConsoleService()
        );

        exitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task Analyze_CliPolicy_OverridesTheConfigFile()
    {
        CreateInconsistentFixture();
        await File.WriteAllTextAsync(
            Path.Combine(_testDirectory, ".cpmigrate.json"),
            """{ "rules": { "VersionInconsistency": "none" } }"""
        );

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--analyze", "--rules", "LicenseRisk=none", "--quiet", "-s", _testDirectory },
            new TestDoubles.FakeConsoleService()
        );

        exitCode
            .Should()
            .Be(
                ExitCodes.AnalysisIssuesFound,
                "the CLI policy replaces the configured one, so VersionInconsistency is armed again"
            );
    }

    [Fact]
    public async Task RunAsync_RulesWithoutAnalyze_WarnsRatherThanSilentlyMigrating()
    {
        CreateInconsistentFixture();
        var console = new TestDoubles.FakeConsoleService();

        await ProgramRunner.RunAsync(
            new[]
            {
                "--rules",
                "VersionInconsistency=none",
                "--dry-run",
                "--quiet",
                "-s",
                _testDirectory,
            },
            console
        );

        console
            .OutputMessages.Should()
            .Contain(message => message.Contains("--rules") && message.Contains("--analyze"));
    }

    [Fact]
    public async Task WriteBaseline_DoesNotRecordFindingsFromADisabledRule()
    {
        // A disabled rule produces no findings at all, so nothing about it belongs in a file that
        // records accepted debt — otherwise re-enabling the rule would find its findings already
        // accepted.
        CreateInconsistentFixture();
        var baselinePath = Path.Combine(_testDirectory, "baseline.json");

        await ProgramRunner.RunAsync(
            new[]
            {
                "--analyze",
                "--write-baseline",
                "--baseline",
                baselinePath,
                "--rules",
                "VersionInconsistency=none",
                "--quiet",
                "-s",
                _testDirectory,
            },
            new TestDoubles.FakeConsoleService()
        );

        var baseline = await File.ReadAllTextAsync(baselinePath);
        baseline.Should().NotContain("VersionInconsistency");
    }

    private Task<int> RunAnalyzeAsync(
        string? rules,
        OutputFormat format = OutputFormat.Terminal,
        FailOnSeverity? failOn = null
    )
    {
        var options = new Options
        {
            Analyze = true,
            Output = format,
            Quiet = true,
            SolutionFileDir = _testDirectory,
            Rules = rules,
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

    private static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var original = Console.Out;
        await using var writer = new StringWriter();
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

        return writer.ToString();
    }

    private void CreateInconsistentFixture()
    {
        CreateProject("Api.csproj", "13.0.1");
        CreateProject("Lib.csproj", "12.0.3");
        CreateSolution("Test.sln", "Api.csproj", "Lib.csproj");
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

    private void CreateProject(string fileName, string version)
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, fileName),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
    }
}
