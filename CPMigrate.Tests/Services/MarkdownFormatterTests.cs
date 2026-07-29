using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// The Markdown report is the one output a human reads without being asked to. That makes two things
/// load-bearing: the verdict has to be visible before any detail, and the table must survive package
/// names and paths that CPMigrate did not write.
/// </summary>
public class MarkdownFormatterTests
{
    [Fact]
    public void Format_NoFindings_LeadsWithASuccessVerdict()
    {
        var markdown = MarkdownFormatter.Format(EmptyReport(), EmptyPackageInfo());

        markdown.Should().StartWith("## ✅ CPMigrate");
        markdown.Should().Contain("No findings");
        markdown.Should().NotContain("| Severity | Rule |", "there is nothing to tabulate");
    }

    [Fact]
    public void Format_FindingAtOrAboveTheThreshold_LeadsWithAFailureVerdict()
    {
        var report = ReportWith(Issue("System.Text.Json", AnalysisSeverity.Critical));

        var markdown = MarkdownFormatter.Format(
            report,
            EmptyPackageInfo(),
            new MarkdownReportContext(FailOn: FailOnSeverity.High)
        );

        markdown.Should().StartWith("## ❌ CPMigrate");
        markdown.Should().Contain("at or above **High**");
    }

    [Fact]
    public void Format_FindingsBelowTheThreshold_ReportsSuccessAndStillListsThem()
    {
        // The distinction the report has to make clear: problems exist, and the build passed anyway.
        var report = ReportWith(Issue("Newtonsoft.Json", AnalysisSeverity.Moderate));

        var markdown = MarkdownFormatter.Format(
            report,
            EmptyPackageInfo(),
            new MarkdownReportContext(FailOn: FailOnSeverity.Critical)
        );

        markdown.Should().StartWith("## ✅ CPMigrate");
        markdown.Should().Contain("No findings reached the failure threshold");
        markdown.Should().Contain("Newtonsoft.Json", "the finding is still reported");
    }

    [Fact]
    public void Format_IncompleteScan_WarnsBeforeTheFindings()
    {
        // An incomplete scan reporting nothing reads exactly like a clean one, so the caveat has to
        // come before a reader draws a conclusion.
        var markdown = MarkdownFormatter.Format(
            EmptyReport(),
            EmptyPackageInfo(),
            new MarkdownReportContext(ScanFailures: 2, DeepScanFailures: 1)
        );

        markdown.Should().StartWith("## ⚠️ CPMigrate");
        markdown.Should().Contain("> [!WARNING]");
        markdown.Should().Contain("2 project(s) failed to scan");
        markdown.Should().Contain("1 package quer(ies) failed");
        markdown.Should().Contain("absence of a finding does not mean absence of a problem");
    }

    [Fact]
    public void Format_NonZeroExitWithNoFindings_IsNotReportedAsClean()
    {
        // The dangerous shape: a run that failed before producing findings looks identical to a
        // clean one. NoProjectsFound (exit 4) is the common case — a misconfigured path.
        var markdown = MarkdownFormatter.Format(
            EmptyReport(),
            EmptyPackageInfo(),
            new MarkdownReportContext(ExitCode: ExitCodes.NoProjectsFound)
        );

        markdown.Should().StartWith("## ⚠️");
        markdown.Should().Contain("Analysis did not complete (exit 4)");
        markdown.Should().Contain("not evidence that the dependencies are healthy");
    }

    [Fact]
    public void Format_NonZeroExitBecauseFindingsWereGated_ReportsTheFindings()
    {
        // Exit 5 with findings is the gate working, not a failed run: the verdict must name the
        // findings rather than claim the analysis did not complete.
        var report = ReportWith(Issue("System.Text.Json", AnalysisSeverity.Critical));

        var markdown = MarkdownFormatter.Format(
            report,
            EmptyPackageInfo(),
            new MarkdownReportContext(
                FailOn: FailOnSeverity.High,
                GatedIssueCount: 1,
                ExitCode: ExitCodes.AnalysisIssuesFound
            )
        );

        markdown.Should().StartWith("## ❌");
        markdown.Should().Contain("at or above **High**");
        markdown.Should().NotContain("did not complete");
    }

    [Fact]
    public void Format_BreaksFindingsDownBySeverity_WorstFirst()
    {
        var report = ReportWith(
            Issue("Low.Package", AnalysisSeverity.Low),
            Issue("Critical.Package", AnalysisSeverity.Critical),
            Issue("Moderate.Package", AnalysisSeverity.Moderate)
        );

        var markdown = MarkdownFormatter.Format(report, EmptyPackageInfo());

        var criticalIndex = markdown.IndexOf("Critical.Package", StringComparison.Ordinal);
        var lowIndex = markdown.IndexOf("Low.Package", StringComparison.Ordinal);
        criticalIndex.Should().BeLessThan(lowIndex, "the worst finding is the one to act on first");

        markdown.Should().Contain("| Severity | Findings |");
    }

    [Fact]
    public void Format_LinksEachFindingToItsRuleDocumentation()
    {
        var report = ReportWith(Issue("Newtonsoft.Json", AnalysisSeverity.Moderate));

        var markdown = MarkdownFormatter.Format(report, EmptyPackageInfo());

        markdown.Should().Contain("[VersionInconsistency](https://");
        markdown.Should().Contain("#versioninconsistency");
    }

    [Fact]
    public void Format_EscapesPipesSoOneOddPackageNameCannotCorruptTheTable()
    {
        // Package names and project paths come from files CPMigrate did not write. A stray pipe
        // silently destroys every row after it.
        var report = ReportWith(
            Issue("Weird|Package", AnalysisSeverity.Moderate) with
            {
                Description = "a | b",
                AffectedProjects = new[] { "src/a|b/App.csproj" },
            }
        );

        var markdown = MarkdownFormatter.Format(report, EmptyPackageInfo());

        markdown.Should().Contain("Weird\\|Package");
        markdown.Should().Contain("a \\| b");
        markdown.Should().Contain("src/a\\|b/App.csproj");
    }

    [Fact]
    public void Format_CollapsesNewlinesInDescriptions()
    {
        var report = ReportWith(
            Issue("Newtonsoft.Json", AnalysisSeverity.Moderate) with
            {
                Description = "first line\nsecond line",
            }
        );

        var markdown = MarkdownFormatter.Format(report, EmptyPackageInfo());

        markdown.Should().Contain("first line second line");
    }

    [Fact]
    public void Format_ManyFindings_CollapsesThemBehindADisclosure()
    {
        // A job summary with hundreds of rows buries everything else on the page.
        var many = Enumerable
            .Range(0, 40)
            .Select(i => Issue($"Package{i}", AnalysisSeverity.Low))
            .ToArray();

        var markdown = MarkdownFormatter.Format(ReportWith(many), EmptyPackageInfo());

        markdown.Should().Contain("<details>");
        markdown.Should().Contain("<summary>All 40 findings</summary>");
        markdown.Should().Contain("</details>");
    }

    [Fact]
    public void Format_FewFindings_ShowsThemWithoutADisclosure()
    {
        var markdown = MarkdownFormatter.Format(
            ReportWith(Issue("Newtonsoft.Json", AnalysisSeverity.Moderate)),
            EmptyPackageInfo()
        );

        markdown.Should().NotContain("<details>");
    }

    [Fact]
    public void Format_ManyAffectedProjects_SummarisesTheTail()
    {
        var report = ReportWith(
            Issue("Newtonsoft.Json", AnalysisSeverity.Moderate) with
            {
                AffectedProjects = Enumerable
                    .Range(1, 8)
                    .Select(i => $"src/P{i}/P{i}.csproj")
                    .ToList(),
            }
        );

        var markdown = MarkdownFormatter.Format(report, EmptyPackageInfo());

        markdown
            .Should()
            .Contain("+5 more", "one finding across forty projects would be unreadable");
        markdown.Should().Contain("src/P1/P1.csproj");
    }

    [Fact]
    public void Format_BaselinedFindings_AreMarkedAndExplained()
    {
        var report = ReportWith(
            Issue("Newtonsoft.Json", AnalysisSeverity.Moderate) with
            {
                Suppressed = true,
            }
        );

        var markdown = MarkdownFormatter.Format(
            report,
            EmptyPackageInfo(),
            new MarkdownReportContext(BaselinePath: ".cpmigrate-baseline.json")
        );

        markdown.Should().Contain("*(baselined)*");
        markdown.Should().Contain("Accepted in baseline");
        markdown.Should().Contain(".cpmigrate-baseline.json");
        markdown.Should().Contain("do not fail the build");
    }

    [Fact]
    public void Format_ReportsScanTotalsAndTheToolVersion()
    {
        var packageInfo = new ProjectPackageInfo(
            new[]
            {
                new PackageReference("Newtonsoft.Json", "13.0.1", "/repo/A.csproj", "A.csproj"),
                new PackageReference("Serilog", "4.3.0", "/repo/B.csproj", "B.csproj"),
            }
        );

        var markdown = MarkdownFormatter.Format(EmptyReport(), packageInfo);

        markdown.Should().Contain("| Projects scanned | 2 |");
        markdown.Should().Contain("| Package references | 2 |");
        markdown.Should().Contain($"CPMigrate {OutputMetadata.CurrentVersion}");
    }

    private static AnalysisReport EmptyReport()
    {
        return new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());
    }

    private static ProjectPackageInfo EmptyPackageInfo()
    {
        return new ProjectPackageInfo(Array.Empty<PackageReference>());
    }

    private static AnalysisReport ReportWith(params AnalysisIssue[] issues)
    {
        return new AnalysisReport(1, issues.Length, new[] { new AnalyzerResult("Stub", issues) });
    }

    private static AnalysisIssue Issue(string package, AnalysisSeverity severity)
    {
        return new AnalysisIssue(
            package,
            $"{package} has a problem.",
            new[] { "src/Api/Api.csproj" },
            AnalysisIssueCode.VersionInconsistency,
            severity
        );
    }
}
