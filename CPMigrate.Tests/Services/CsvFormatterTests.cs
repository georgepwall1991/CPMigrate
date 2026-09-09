using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// CSV is the format a spreadsheet opens without asking questions, so the contract that matters
/// is structural: a stable header, one row per finding, and escaping that survives commas,
/// quotes, and line breaks in package names and descriptions CPMigrate did not write.
/// </summary>
public class CsvFormatterTests
{
    [Fact]
    public void Format_EmptyReport_EmitsOnlyTheHeader()
    {
        var csv = CsvFormatter.Format(EmptyReport(), EmptyPackageInfo());

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
        lines[0].TrimEnd('\r').Should().Be("Rule,Severity,Package,Description,AffectedProjects,Fixable");
    }

    [Fact]
    public void Format_SingleFinding_EmitsOneRowInColumnOrder()
    {
        var report = ReportWith(Issue("Newtonsoft.Json", AnalysisSeverity.High, fixable: true));

        var csv = CsvFormatter.Format(report, EmptyPackageInfo());

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        var columns = lines[1].TrimEnd('\r').Split(',');
        columns.Should().HaveCount(6);
        columns[0].Should().Be("Stub");
        columns[1].Should().Be("High");
        columns[2].Should().Be("Newtonsoft.Json");
        columns[4].Should().Be("src/Api/Api.csproj");
        columns[5].Should().Be("true");
    }

    [Fact]
    public void Format_UnfixableFinding_RendersFalse()
    {
        var report = ReportWith(Issue("Serilog", AnalysisSeverity.Low, fixable: false));

        var csv = CsvFormatter.Format(report, EmptyPackageInfo());

        csv.Should().Contain(",false");
    }

    [Fact]
    public void Format_CommaInDescription_QuotesTheField()
    {
        var issue = Issue("Newtonsoft.Json", AnalysisSeverity.Moderate) with
        {
            Description = "drifted across projects, needs a pin"
        };
        var report = ReportWith(issue);

        var csv = CsvFormatter.Format(report, EmptyPackageInfo());

        csv.Should().Contain("\"drifted across projects, needs a pin\"");
    }

    [Fact]
    public void Format_QuoteInValue_DoublesItAndQuotesTheField()
    {
        var issue = Issue("Newtonsoft.Json", AnalysisSeverity.Moderate) with
        {
            Description = "the \"pinned\" version wins"
        };
        var report = ReportWith(issue);

        var csv = CsvFormatter.Format(report, EmptyPackageInfo());

        csv.Should().Contain("\"the \"\"pinned\"\" version wins\"");
    }

    [Fact]
    public void Format_NewlineInValue_QuotesTheField()
    {
        var issue = Issue("Newtonsoft.Json", AnalysisSeverity.Moderate) with
        {
            Description = "first line\nsecond line"
        };
        var report = ReportWith(issue);

        var csv = CsvFormatter.Format(report, EmptyPackageInfo());
    }

    [Fact]
    public void Format_CarriageReturnInValue_QuotesTheField()
    {
        // A bare \r outside quotes silently breaks row parsing in strict CSV readers,
        // so it must trigger quoting exactly like \n does.
        var issue = Issue("Newtonsoft.Json", AnalysisSeverity.Moderate) with
        {
            Description = "first line\rsecond line"
        };
        var report = ReportWith(issue);

        var csv = CsvFormatter.Format(report, EmptyPackageInfo());
    }

    [Fact]
    public void Format_MultipleProjects_JoinsThemWithSemicolonsInOneField()
    {
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "drifted.",
            new[] { "src/A/A.csproj", "src/B/B.csproj" },
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.High);
        var report = ReportWith(issue);

        var csv = CsvFormatter.Format(report, EmptyPackageInfo());

        csv.Should().Contain("src/A/A.csproj; src/B/B.csproj");
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

    private static AnalysisIssue Issue(string package, AnalysisSeverity severity, bool fixable = false)
    {
        return new AnalysisIssue(
            package,
            $"{package} has a problem.",
            new[] { "src/Api/Api.csproj" },
            AnalysisIssueCode.VersionInconsistency,
            severity,
            fixable);
    }
}
