using System.Text.Json;
using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Verify;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The published contract of <c>--verify</c>: what it refuses, what it exits with, and what it puts in
/// the payload.
/// </summary>
public class VerifyContractTests : IDisposable
{
    private readonly string _testDirectory;

    public VerifyContractTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateVerify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ── What it refuses, and by name ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(
        "--dry-run",
        "--dry-run",
        "a dry run writes nothing, so there is nothing to verify"
    )]
    [InlineData(
        "--analyze",
        "--analyze",
        "analysis does not migrate, so there is no migration to verify"
    )]
    [InlineData("--rollback", "--rollback", "rollback runs instead of a migration")]
    [InlineData("--unify-props", "--unify-props", "unify-props runs instead of a migration")]
    public void Rejects_CombinationsWhereVerificationCouldNotRun(
        string flag,
        string expectedInMessage,
        string why
    )
    {
        // Named rather than ignored. A run that silently skipped verification would be
        // indistinguishable from one that verified and found nothing — which is the failure this
        // feature exists to prevent, turned on itself.
        var options = OptionsWith(flag);
        options.Verify = true;

        var act = options.Validate;

        act.Should().Throw<ArgumentException>(why).WithMessage($"*{expectedInMessage}*");
    }

    [Fact]
    public void Accepts_InteractiveConflicts_WhichModifiesAMigrationRatherThanReplacingOne()
    {
        // The rejection list for --verify is "modes that run instead of a migration", which is a
        // different question from the one --output Sarif asks ("modes that run instead of an
        // analysis"). --interactive-conflicts is on the second list and not the first: choosing the
        // winning version by hand is an ordinary way to migrate, and it is exactly the case the
        // report describes as an interactive decision. Reusing the analysis list here rejected the
        // combination the attribution code was written for.
        var options = new Options { Verify = true, InteractiveConflicts = true };

        var act = options.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("--doctor")]
    [InlineData("--init")]
    [InlineData("--status")]
    [InlineData("--tree")]
    public async Task Rejects_DiagnosticModes_BeforeTheyRun(string mode)
    {
        // These return from ProgramRunner without reaching the router, so a rejection enforced only
        // downstream let `--verify --init` write a config file and exit 0 with the verification
        // silently dropped. For a flag whose whole purpose is to refuse to call an unmeasured run
        // clean, being quietly ignored is the one failure it must not have. Cross-review caught it.
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            ["--verify", mode, "--force", "-s", _testDirectory],
            console
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        console.ErrorMessages.Should().Contain(m => m.Contains(mode));
        File.Exists(Path.Combine(_testDirectory, ".cpmigrate.json"))
            .Should()
            .BeFalse("the rejected command must not have run");
    }

    [Fact]
    public void Rejects_VerifyWithBatch()
    {
        // Each solution would capture and compare its own graph, but the batch payload has no shape
        // for the result — so it would cost two restores per solution and report nothing.
        var options = new Options { Verify = true, BatchDir = _testDirectory };

        var act = options.Validate;

        act.Should().Throw<ArgumentException>().WithMessage("*--batch*");
    }

    [Fact]
    public void Rejects_CsvOutput_WhichHasNoShapeForAReceipt()
    {
        // The CSV writer serializes analyzer findings and a migration has none, so the run would pay
        // for both restores, reach a verdict, and write an empty file. A document that silently
        // contains no verification is worse than a rejection. Cross-review caught it.
        var options = new Options { Verify = true, Output = OutputFormat.Csv };

        var act = options.Validate;

        act.Should().Throw<ArgumentException>().WithMessage("*Csv*");
    }

    [Fact]
    public void Rejects_StrictWithoutVerify()
    {
        var options = new Options { VerifyStrict = true };

        var act = options.Validate;

        act.Should().Throw<ArgumentException>().WithMessage("*--verify-strict requires --verify*");
    }

    [Fact]
    public void Accepts_MarkdownOutputForAVerifyingMigration()
    {
        // The receipt is the artefact most worth pasting into a pull request, so Markdown stops
        // meaning "analyzer findings only" once --verify is in play.
        var options = new Options { Verify = true, Output = OutputFormat.Markdown };

        var act = options.Validate;

        act.Should().NotThrow();
    }

    [Fact]
    public void StillRejects_MarkdownOutputForAPlainMigration()
    {
        // The relaxation above must not become "Markdown always allowed": a plain migration produces
        // no report at all, and an empty document is worse than a rejection.
        var options = new Options { Output = OutputFormat.Markdown };

        var act = options.Validate;

        act.Should().Throw<ArgumentException>().WithMessage("*--analyze*");
    }

    [Fact]
    public async Task RejectsBeforeMigrating_RatherThanAfterwards()
    {
        // Driven through the real entry point without a console double, because the assertion is that
        // nothing was written — and a rejection that arrives after the rewrite has already rejected
        // nothing. A fake console has hidden exactly this class of defect in this repository before.
        CreateProject("A.csproj", "Serilog", "4.3.0");

        var exitCode = await ProgramRunner.RunAsync(
            ["--verify", "--dry-run", "-s", _testDirectory],
            new FakeConsoleService()
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        File.Exists(Path.Combine(_testDirectory, "Directory.Packages.props")).Should().BeFalse();
    }

    // ── The restore target ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("src/Solution.slnx")]
    [InlineData("Solution.slnx")]
    [InlineData("./nested/deeper/Solution.slnx")]
    public void MakesTheRestoreTargetAbsolute(string relativeTarget)
    {
        // RunRestoreAsync runs from the target's own directory while passing the path through as the
        // argument, so a relative `-s src/Solution.slnx` resolved to `src/src/Solution.slnx` and the
        // baseline restore failed — reported as "this solution does not restore" about a solution
        // that restores perfectly well, which is the worst kind of finding this feature can produce.
        // Cross-review caught it.
        //
        // Asserted here rather than end to end, because an end-to-end version has to put a project
        // somewhere a relative path can reach: under the working directory it inherits this
        // repository's own Directory.Packages.props and migrates something else entirely, and in the
        // system temp directory there may be no relative form at all — on Windows those are commonly
        // different volumes. Both were tried; both tested the environment rather than the fix.
        var options = new Options { SolutionFileDir = relativeTarget };

        var target = MigrationService.RestoreTarget(options);

        Path.IsPathRooted(target).Should().BeTrue();
        target.Should().EndWith(Path.GetFileName(relativeTarget));
    }

    [Fact]
    public void LeavesAnAbsoluteRestoreTargetAlone()
    {
        var absolute = Path.Combine(_testDirectory, "Solution.slnx");

        MigrationService
            .RestoreTarget(new Options { SolutionFileDir = absolute })
            .Should()
            .Be(Path.GetFullPath(absolute));
    }

    // ── The exit-code matrix ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(VerificationVerdict.Unchanged, false, true, false)]
    [InlineData(VerificationVerdict.Unchanged, true, true, false)]
    [InlineData(VerificationVerdict.ExplainedDrift, false, true, false)]
    [InlineData(VerificationVerdict.ExplainedDrift, true, false, false)]
    [InlineData(VerificationVerdict.UnexplainedDrift, false, false, true)]
    [InlineData(VerificationVerdict.UnexplainedDrift, true, false, true)]
    [InlineData(VerificationVerdict.Failed, false, false, true)]
    [InlineData(VerificationVerdict.Failed, true, false, true)]
    public void MapsEachVerdictToAGateAndARollback(
        VerificationVerdict verdict,
        bool strict,
        bool expectedPass,
        bool expectedRollback
    )
    {
        // Passing and rolling back are deliberately different questions. Drift that fails only
        // because of --verify-strict is drift the report can account for, and the tree is left in
        // place so the person who asked for a no-op can see what stopped it being one.
        var report = Report(verdict);

        report.Passed(strict).Should().Be(expectedPass);
        report.ShouldRollBack.Should().Be(expectedRollback);
    }

    [Fact]
    public void AFailedVerdictIsNeverClean()
    {
        // The whole point. "Could not establish that the graph is unchanged" must never be reported
        // the same way as "the graph is unchanged".
        var report = Report(VerificationVerdict.Failed);

        report.Passed(strict: false).Should().BeFalse();
        report.Passed(strict: true).Should().BeFalse();
    }

    // ── The payload ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PayloadIsAbsent_WhenTheRunDidNotVerify()
    {
        // Absence is meaningful, the same way summary.disabledRules is: it is the only way a consumer
        // can tell a clean verification from no verification at all.
        VerificationPayload.From(null, strict: false).Should().BeNull();
    }

    [Fact]
    public void PayloadCarriesTheVerdictTheGateUsed()
    {
        var payload = VerificationPayload.From(
            Report(VerificationVerdict.ExplainedDrift),
            strict: true
        )!;

        payload.Verdict.Should().Be("explainedDrift");
        payload.Passed.Should().BeFalse("--verify-strict was in force");
        payload.Strict.Should().BeTrue();
    }

    [Fact]
    public void PayloadSerializesEnumsAsCamelCase()
    {
        // The wire contract is camelCase throughout; leaking C# naming through it would make the
        // published enum values a hostage to an internal rename.
        var change = new AttributedChange(
            new GraphChange("A.csproj", "net10.0", "Serilog", "3.1.1", "4.4.0", IsDirect: true),
            DriftExplanation.ConflictUnified,
            CausedBy: null,
            "conflict unified"
        );

        var payload = VerificationPayload.From(
            Report(VerificationVerdict.ExplainedDrift, change),
            strict: false
        )!;

        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement.GetProperty("changes")[0];

        row.GetProperty("kind").GetString().Should().Be("changed");
        row.GetProperty("direction").GetString().Should().Be("upgrade");
        row.GetProperty("explanation").GetString().Should().Be("conflictUnified");
    }

    [Fact]
    public void PayloadRecordsWhichVersionWonAndOutOfWhat()
    {
        // Until this existed the payload carried a conflictsResolved integer and nothing else, which
        // cannot answer the only question a reviewer of a migration PR has.
        var decision = new MigrationDecision(
            "Serilog",
            "4.4.0",
            [
                new VersionCandidate("3.1.1", ["Api.csproj"]),
                new VersionCandidate("4.4.0", ["Worker.csproj", "Web.csproj"]),
            ],
            ConflictDecisionSource.Highest
        );

        var payload = VerificationPayload.From(
            Report(VerificationVerdict.ExplainedDrift, decisions: [decision]),
            strict: false
        )!;

        var recorded = payload.Decisions.Single();
        recorded.PackageId.Should().Be("Serilog");
        recorded.ResolvedVersion.Should().Be("4.4.0");
        recorded.Source.Should().Be("highest");
        recorded.Candidates.Should().HaveCount(2);
        recorded.Candidates.Single(c => c.Version == "4.4.0").Projects.Should().HaveCount(2);
    }

    [Fact]
    public void PayloadOmitsIntegrityFailures_WhenThereWereNone()
    {
        VerificationPayload
            .From(Report(VerificationVerdict.Unchanged), strict: false)!
            .IntegrityFailures.Should()
            .BeNull();
    }

    [Fact]
    public void PayloadMapsEveryIntegrityFailureField()
    {
        var report = new VerificationReport(
            VerificationVerdict.Failed,
            ProjectsRestored: 1,
            ProjectsExpected: 2,
            ResolvedVersionCount: 10,
            UnchangedCount: 9,
            Changes: [],
            IntegrityFailures:
            [
                new GraphIntegrityFailure(
                    "src/Worker/Worker.csproj",
                    "net10.0",
                    "framework was not restored"
                ),
            ],
            Decisions: [],
            FailureReason: "the captures did not cover the same frameworks"
        );

        var failure = VerificationPayload
            .From(report, strict: false)!
            .IntegrityFailures.Should()
            .ContainSingle()
            .Which;

        failure.Project.Should().Be("src/Worker/Worker.csproj");
        failure.TargetFramework.Should().Be("net10.0");
        failure.Reason.Should().Be("framework was not restored");
    }

    // ── The Markdown receipt ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkdownNamesUnexplainedChangesAsSuch()
    {
        var change = new AttributedChange(
            new GraphChange("A.csproj", "net10.0", "Mystery", "1.0.0", "2.0.0", IsDirect: false),
            DriftExplanation.Unexplained,
            CausedBy: null,
            "nothing this migration decided accounts for this change"
        );

        var markdown = VerificationMarkdown.Format(
            Report(VerificationVerdict.UnexplainedDrift, change),
            strict: false
        );

        markdown.Should().Contain("unexplained").And.Contain("Mystery");
    }

    [Fact]
    public void MarkdownEscapesAPipe_SoOneCannotShiftEveryColumn()
    {
        var change = new AttributedChange(
            new GraphChange("A.csproj", "net10.0", "Odd|Name", "1.0.0", "2.0.0", IsDirect: false),
            DriftExplanation.Unexplained,
            CausedBy: null,
            "unexplained"
        );

        var markdown = VerificationMarkdown.Format(
            Report(VerificationVerdict.UnexplainedDrift, change),
            strict: false
        );

        markdown.Should().Contain("Odd\\|Name");
    }

    [Fact]
    public void MarkdownStatesWhatItDropped_RatherThanTruncatingSilently()
    {
        // A table that stops without saying so reads as a complete one.
        var changes = Enumerable
            .Range(0, 60)
            .Select(i => new AttributedChange(
                new GraphChange("A.csproj", "net10.0", $"Package{i:D2}", "1.0.0", "2.0.0", false),
                DriftExplanation.Unexplained,
                null,
                "unexplained"
            ))
            .ToArray();

        var markdown = VerificationMarkdown.Format(
            Report(VerificationVerdict.UnexplainedDrift, changes),
            strict: false
        );

        markdown.Should().Contain("Showing 50 of 60");
    }

    private static VerificationReport Report(
        VerificationVerdict verdict,
        params AttributedChange[] changes
    ) => Report(verdict, changes, []);

    private static VerificationReport Report(
        VerificationVerdict verdict,
        IReadOnlyList<MigrationDecision> decisions
    ) => Report(verdict, [], decisions);

    private static VerificationReport Report(
        VerificationVerdict verdict,
        IReadOnlyList<AttributedChange> changes,
        IReadOnlyList<MigrationDecision> decisions
    ) =>
        new(
            verdict,
            ProjectsRestored: 1,
            ProjectsExpected: 1,
            ResolvedVersionCount: 10,
            UnchangedCount: 9,
            changes,
            [],
            decisions,
            verdict == VerificationVerdict.Failed ? "restore failed" : null
        );

    private static Options OptionsWith(string flag)
    {
        var options = new Options();

        switch (flag)
        {
            case "--dry-run":
                options.DryRun = true;
                break;
            case "--analyze":
                options.Analyze = true;
                break;
            case "--rollback":
                options.Rollback = true;
                break;
            case "--unify-props":
                options.UnifyProps = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(flag), flag, "unhandled flag");
        }

        return options;
    }

    private void CreateProject(string name, string package, string version)
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, name),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{package}" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
    }
}
