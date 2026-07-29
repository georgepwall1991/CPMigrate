using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Models;

/// <summary>
/// Severity filtering is what makes a CI gate adoptable: a team with existing debt needs to fail on
/// vulnerabilities without also failing on every informational finding. These tests pin the
/// counting behaviour the <c>--fail-on</c> threshold is built on.
/// </summary>
public class AnalysisReportSeverityTests
{
    [Theory]
    [InlineData(AnalysisSeverity.Info, 5)]
    [InlineData(AnalysisSeverity.Low, 4)]
    [InlineData(AnalysisSeverity.Moderate, 3)]
    [InlineData(AnalysisSeverity.High, 2)]
    [InlineData(AnalysisSeverity.Critical, 1)]
    public void CountAtOrAbove_CountsEveryIssueAtOrAboveTheThreshold(
        AnalysisSeverity threshold,
        int expected
    )
    {
        var report = ReportWithOneIssuePerSeverity();

        report.CountAtOrAbove(threshold).Should().Be(expected);
    }

    [Fact]
    public void CountAtOrAbove_EmptyReport_IsZeroAtEverySeverity()
    {
        var report = new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());

        foreach (var severity in Enum.GetValues<AnalysisSeverity>())
        {
            report.CountAtOrAbove(severity).Should().Be(0);
        }
    }

    [Fact]
    public void CountAtOrAbove_SumsAcrossAnalyzers()
    {
        var report = new AnalysisReport(
            2,
            4,
            new[]
            {
                new AnalyzerResult("First", new[] { Issue(AnalysisSeverity.High) }),
                new AnalyzerResult(
                    "Second",
                    new[] { Issue(AnalysisSeverity.High), Issue(AnalysisSeverity.Low) }
                ),
            }
        );

        report.CountAtOrAbove(AnalysisSeverity.High).Should().Be(2);
        report.CountAtOrAbove(AnalysisSeverity.Info).Should().Be(3);
    }

    [Fact]
    public void HighestSeverity_ReportsTheWorstFinding()
    {
        var report = new AnalysisReport(
            1,
            1,
            new[]
            {
                new AnalyzerResult(
                    "First",
                    new[] { Issue(AnalysisSeverity.Low), Issue(AnalysisSeverity.High) }
                ),
            }
        );

        report.HighestSeverity.Should().Be(AnalysisSeverity.High);
    }

    [Fact]
    public void HighestSeverity_EmptyReport_IsNull()
    {
        var report = new AnalysisReport(0, 0, Array.Empty<AnalyzerResult>());

        report.HighestSeverity.Should().BeNull();
    }

    private static AnalysisReport ReportWithOneIssuePerSeverity()
    {
        return new AnalysisReport(
            1,
            5,
            new[]
            {
                new AnalyzerResult(
                    "All severities",
                    Enum.GetValues<AnalysisSeverity>().Select(Issue).ToList()
                ),
            }
        );
    }

    private static AnalysisIssue Issue(AnalysisSeverity severity)
    {
        return new AnalysisIssue(
            $"Package.{severity}",
            $"A {severity} finding.",
            new[] { "Sample.csproj" },
            AnalysisIssueCode.VersionInconsistency,
            severity
        );
    }
}
