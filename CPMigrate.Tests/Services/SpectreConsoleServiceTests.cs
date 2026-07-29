using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace CPMigrate.Tests.Services;

// Console-rendering assertions are sensitive to the ambient Spectre console: the contract
// tests replace AnsiConsole.Console wholesale, and an interleaved run here saw every
// assertion in this class fail at once. Sharing the sequential collection removes the race.
[Collection("Sequential")]
public class SpectreConsoleServiceTests
{
    private readonly TestConsole _console;
    private readonly VersionResolver _versionResolver;
    private readonly SpectreConsoleService _service;

    public SpectreConsoleServiceTests()
    {
        _console = new TestConsole().Interactive();
        _versionResolver = new VersionResolver();
        _service = new SpectreConsoleService(_versionResolver, _console);
    }

    [Fact]
    public void Info_WritesCorrectMarkup()
    {
        _service.Info("Test Message");
        _console.Output.Should().Contain("›");
        _console.Output.Should().Contain("Test Message");
    }

    [Fact]
    public void Success_WritesCorrectMarkup()
    {
        _service.Success("Perfect");
        _console.Output.Should().Contain("✔");
        _console.Output.Should().Contain("Perfect");
    }

    [Fact]
    public void Warning_WritesCorrectMarkup()
    {
        _service.Warning("Careful");
        _console.Output.Should().Contain("!");
        _console.Output.Should().Contain("Careful");
    }

    [Fact]
    public void Error_WritesCorrectMarkup()
    {
        _service.Error("Broken");
        _console.Output.Should().Contain("✖");
        _console.Output.Should().Contain("Broken");
    }

    [Fact]
    public void Highlight_WritesCorrectMarkup()
    {
        _service.Highlight("Look at this");
        _console.Output.Should().Contain("» Look at this");
    }

    [Fact]
    public void Dim_WritesCorrectMarkup()
    {
        _service.Dim("Muted text");
        _console.Output.Should().Contain("Muted text");
    }

    [Fact]
    public void DryRun_WritesCorrectMarkup()
    {
        _service.DryRun("Simulating...");
        _console.Output.Should().Contain("SIMULATION");
        _console.Output.Should().Contain("Simulating...");
    }

    [Fact]
    public void WriteHeader_WritesLogoAndSystemInfo()
    {
        _service.WriteHeader();
        // Figlet renders text as ASCII art, so we don't look for the literal string.
        // But the Rule text should be present.
        _console.Output.Should().Contain("CENTRAL PACKAGE MANAGEMENT MIGRATION TOOL");
    }

    [Fact]
    public void Banner_WritesPanel()
    {
        _service.Banner("ANNOUNCEMENT");
        _console.Output.Should().Contain("ANNOUNCEMENT");
    }

    [Fact]
    public void Separator_WritesRule()
    {
        _service.Separator();
        _console.Output.Should().NotBeEmpty();
    }

    [Fact]
    public void WriteConflictsTable_WritesTableWithVersions()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Newtonsoft.Json"] = new HashSet<string> { "13.0.1", "12.0.3" }
        };
        var conflicts = new List<string> { "Newtonsoft.Json" };

        _service.WriteConflictsTable(packageVersions, conflicts, ConflictStrategy.Highest);

        _console.Output.Should().Contain("VERSION CONFLICTS");
        _console.Output.Should().Contain("Newtonsoft.Json");
        _console.Output.Should().Contain("13.0.1");
        _console.Output.Should().Contain("12.0.3");
    }

    [Fact]
    public void WriteSummaryTable_DryRun_WritesDryRunLayout()
    {
        _service.WriteSummaryTable(10, 50, 5, "Directory.Packages.props", null, true);
        _console.Output.Should().Contain("SIMULATION RESULTS");
        _console.Output.Should().Contain("DRY RUN COMPLETE");
        _console.Output.Should().Contain("10");
        _console.Output.Should().Contain("50");
        _console.Output.Should().Contain("5");
    }

    [Fact]
    public void WriteSummaryTable_Success_WritesSuccessLayout()
    {
        _service.WriteSummaryTable(10, 50, 5, "Directory.Packages.props", "/backup/path", false);
        _console.Output.Should().Contain("MIGRATION RESULTS");
        _console.Output.Should().Contain("SUCCESS");
        _console.Output.Should().Contain("/backup/path");
    }

    [Fact]
    public void WriteProjectTree_WritesTree()
    {
        var projects = new List<string> { Path.Combine("base", "Proj1.csproj"), Path.Combine("base", "Proj2.csproj") };
        _service.WriteProjectTree(projects, "base");
        _console.Output.Should().Contain("base");
        _console.Output.Should().Contain("Proj1.csproj");
        _console.Output.Should().Contain("Proj2.csproj");
    }

    [Fact]
    public void WritePropsPreview_WritesPanel()
    {
        _service.WritePropsPreview("PACKAGE_CONTENT_PREVIEW");
        _console.Output.Should().Contain("PACKAGE_CONTENT_PREVIEW");
    }

    [Fact]
    public void WriteMissionStatus_WritesStatusGrid()
    {
        _service.WriteMissionStatus(2); // BACKUP
        _console.Output.Should().Contain("DISCOVERY");
        _console.Output.Should().Contain("ANALYSIS");
        _console.Output.Should().Contain("BACKUP");
    }

    [Fact]
    public void WriteRiskScore_LowRisk_WritesCorrectDesc()
    {
        _service.WriteRiskScore(0, 10);
        _console.Output.Should().Contain("LOW");
        _console.Output.Should().Contain("Clean migration path");
    }

    [Fact]
    public void WriteRiskScore_HighRisk_WritesCorrectDesc()
    {
        _service.WriteRiskScore(10, 10);
        _console.Output.Should().Contain("HIGH");
        _console.Output.Should().Contain("Significant version conflicts");
    }

    [Fact]
    public void AskSelection_ReturnsChoice()
    {
        _console.Input.PushKey(ConsoleKey.Enter);
        var result = _service.AskSelection("Pick one", new[] { "Option A", "Option B" });
        result.Should().Be("Option A");
    }

    [Fact]
    public void AskGroupedSelection_ReturnsChoice()
    {
        _console.Input.PushKey(ConsoleKey.Enter);
        var groups = new Dictionary<string, IEnumerable<string>>
        {
            ["Group 1"] = new[] { "Choice 1" }
        };
        var result = _service.AskGroupedSelection("Pick one", groups);
        result.Should().Be("Choice 1");
    }

    [Fact]
    public void AskConfirmation_Yes_ReturnsTrue()
    {
        _console.Input.PushKey(ConsoleKey.Enter);
        var result = _service.AskConfirmation("Sure?");
        result.Should().BeTrue();
    }

    [Fact]
    public void AskText_ReturnsInput()
    {
        _console.Input.PushText("User input");
        _console.Input.PushKey(ConsoleKey.Enter);
        var result = _service.AskText("What text?");
        result.Should().Be("User input");
    }

    [Fact]
    public void AskInt_ReturnsInput()
    {
        _console.Input.PushText("42");
        _console.Input.PushKey(ConsoleKey.Enter);
        var result = _service.AskInt("What number?", 0);
        result.Should().Be(42);
    }

    [Fact]
    public void WriteRollbackPreview_WritesTable()
    {
        var files = new[] { "File1.cs", "File2.cs" };
        _service.WriteRollbackPreview(files, "props.file");
        _console.Output.Should().Contain("ROLLBACK PREVIEW");
        _console.Output.Should().Contain("File1.cs");
        _console.Output.Should().Contain("DELETE");
    }

    [Fact]
    public void WriteAnalysisHeader_WritesPanel()
    {
        _service.WriteAnalysisHeader(5, 20, 1);
        _console.Output.Should().Contain("ANALYSIS MODE");
        _console.Output.Should().Contain("vulnerabilities found");
    }

    [Fact]
    public void WriteAnalyzerResult_HasIssues_WritesTable()
    {
        var result = new AnalyzerResult
        (
            "TestAnalyzer",
            new List<AnalysisIssue>
            {
                new AnalysisIssue("BadPkg", "Too old", new List<string>())
            }
        );

        _service.WriteAnalyzerResult(result);

        _console.Output.Should().Contain("TestAnalyzer");
        _console.Output.Should().Contain("BadPkg");
        _console.Output.Should().Contain("Too old");
    }

    [Fact]
    public void WriteAnalyzerResult_NoIssues_WritesSuccessMessage()
    {
        var result = new AnalyzerResult("GoodAnalyzer", new List<AnalysisIssue>());
        _service.WriteAnalyzerResult(result);
        _console.Output.Should().Contain("GoodAnalyzer");
        _console.Output.Should().Contain("No issues");
    }

    [Fact]
    public void WriteAnalysisSummary_HasIssues_WritesYellowRule()
    {
        var report = new AnalysisReport(1, 1, new List<AnalyzerResult>
            {
                new AnalyzerResult("R", new List<AnalysisIssue> { new AnalysisIssue("P", "D", new List<string>()) })
            });
        _service.WriteAnalysisSummary(report);
        _console.Output.Should().Contain("ANALYSIS COMPLETE");
        _console.Output.Should().Contain("ISSUES");
    }

    [Fact]
    public void WriteLine_WritesText()
    {
        _service.WriteLine("Hello World");
        _console.Output.Should().Contain("Hello World");
    }

    [Fact]
    public void WriteMarkup_WritesMarkup()
    {
        _service.WriteMarkup("[blue]Blue Text[/]");
        _console.Output.Should().Contain("Blue Text");
    }

    [Fact]
    public void WriteAnalysisSummary_HasIssues_WritesPerAnalyzerBreakdown()
    {
        var report = new AnalysisReport(1, 1, new List<AnalyzerResult>
        {
            new AnalyzerResult("Noisy", new List<AnalysisIssue>
            {
                new AnalysisIssue("P1", "D1", new List<string>()),
                new AnalysisIssue("P2", "D2", new List<string>())
            }),
            new AnalyzerResult("Quiet", new List<AnalysisIssue>())
        });

        _service.WriteAnalysisSummary(report);

        _console.Output.Should().Contain("ANALYZER");
        _console.Output.Should().Contain("Noisy");
        _console.Output.Should().Contain("Quiet");
    }

    [Fact]
    public void WriteAnalysisSummary_NoIssues_OmitsBreakdown()
    {
        var report = new AnalysisReport(1, 1, new List<AnalyzerResult>
        {
            new AnalyzerResult("Quiet", new List<AnalysisIssue>())
        });

        _service.WriteAnalysisSummary(report);

        _console.Output.Should().Contain("NO ISSUES");
        _console.Output.Should().NotContain("ANALYZER");
    }

    [Fact]
    public void StatusGlyphs_FallBackToAscii_WhenConsoleLacksUnicode()
    {
        var ascii = new TestConsole().Interactive();
        ascii.Profile.Capabilities.Unicode = false;
        var service = new SpectreConsoleService(_versionResolver, ascii);

        service.Success("Perfect");
        service.Error("Broken");

        ascii.Output.Should().NotContain("✔");
        ascii.Output.Should().NotContain("✖");
        ascii.Output.Should().Contain("Perfect");
        ascii.Output.Should().Contain("Broken");
    }

    [Fact]
    public void WriteRiskScore_RendersMeterAndScore()
    {
        _service.WriteRiskScore(10, 10);
        _console.Output.Should().Contain("100/100");
    }

    [Fact]
    public void WriteStatusDashboard_WritesDashboard()
    {
        var solutions = new List<string> { "S1.sln" };
        var backups = new List<BackupSetInfo> { new BackupSetInfo { Timestamp = "20230101120000", Files = new List<string>() } };
        var tfms = new Dictionary<string, int> { ["net6.0"] = 1 };

        _service.WriteStatusDashboard("/dir", solutions, backups, true, true, tfms);

        _console.Output.Should().Contain("REPOSITORY CONTEXT");
        _console.Output.Should().Contain("/dir");
        _console.Output.Should().Contain("Dirty");
        _console.Output.Should().Contain("net6.0");
    }
}
