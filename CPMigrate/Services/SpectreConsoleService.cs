using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

public class SpectreConsoleService : IConsoleService
{
    private readonly VersionResolver _versionResolver;
    private readonly IAnsiConsole _console;

    public SpectreConsoleService(VersionResolver versionResolver, IAnsiConsole? console = null)
    {
        _versionResolver = versionResolver;
        _console = console ?? AnsiConsole.Console;
    }

    public void Info(string message)
    {
        _console.MarkupLine($"[grey39]›[/] [dim]{EscapeMarkup(message)}[/]");
    }

    public void Success(string message)
    {
        _console.MarkupLine($"[springgreen1]✔[/] [white]{EscapeMarkup(message)}[/]");
    }

    public void Warning(string message)
    {
        _console.MarkupLine($"[orange1]![/] [yellow]{EscapeMarkup(message)}[/]");
    }

    public void Error(string message)
    {
        _console.MarkupLine($"[red1]✖[/] [red]{EscapeMarkup(message)}[/]");
    }

    public void Highlight(string message)
    {
        _console.MarkupLine($"[deeppink1]» {EscapeMarkup(message)}[/]");
    }

    public void Dim(string message)
    {
        _console.MarkupLine($"[grey39]{EscapeMarkup(message)}[/]");
    }

    public void DryRun(string message)
    {
        _console.MarkupLine($"  [cyan1]○[/] [blue]SIMULATION[/] [grey]{EscapeMarkup(message)}[/]");
    }

    public void WriteHeader()
    {
        _console.Clear();
        _console.WriteLine();

        _console.Write(new FigletText("CPMigrate")
            .LeftJustified()
            .Color(SpectrePalette.CyberColors.Primary));

        var rule = new Rule("[cyan1]CENTRAL PACKAGE MANAGEMENT MIGRATION TOOL[/]")
        {
            Style = Style.Parse("grey39"),
        };
        _console.Write(rule);

        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";

        var grid = new Grid();
        grid.AddColumn(new GridColumn().RightAligned());
        grid.AddRow($"[grey39]v{version}[/] [deepskyblue1]•[/] [grey39]{runtime}[/] [deepskyblue1]•[/] [grey39]{os}[/]");

        _console.Write(grid);
        _console.WriteLine();
    }

    public void Banner(string message)
    {
        _console.Write(SpectrePanelBuilder.BuildBannerPanel(message));
    }

    public void Separator()
    {
        _console.Write(new Rule { Style = Style.Parse("grey39") });
    }

    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts, ConflictStrategy strategy)
    {
        var table = SpectreTableBuilder.BuildConflictsTable(packageVersions, conflicts, strategy, _versionResolver);
        _console.WriteLine();
        _console.Write(table);
        _console.WriteLine();
    }

    public void WriteSummaryTable(int projectCount, int packageCount, int conflictCount,
        string propsFilePath, string? backupPath, bool wasDryRun)
    {
        _console.WriteLine();
        _console.Write(SpectrePanelBuilder.BuildSummaryRule(wasDryRun));
        _console.Write(SpectrePanelBuilder.BuildSummaryPanel(projectCount, packageCount, conflictCount, propsFilePath, backupPath, wasDryRun));
        if (wasDryRun)
        {
            _console.MarkupLine(SpectrePanelBuilder.DryRunHint);
        }
    }

    public void WriteProjectTree(List<string> projectPaths, string basePath)
    {
        _console.WriteLine();
        _console.Write(SpectreTableBuilder.BuildProjectTree(projectPaths, basePath));
        _console.WriteLine();
    }

    public void WritePropsPreview(string content)
    {
        _console.Write(SpectrePanelBuilder.BuildPropsPreviewPanel(content));
    }

    public void WriteMarkup(string message)
    {
        _console.MarkupLine(message);
    }

    public void WriteLine(string message = "")
    {
        _console.WriteLine(message);
    }

    public void WriteMissionStatus(int step)
    {
        _console.Write(SpectrePanelBuilder.BuildMissionStatusPanel(step));
        _console.WriteLine();
    }

    public void WriteRiskScore(int conflictCount, int projectCount)
    {
        _console.Write(SpectrePanelBuilder.BuildRiskScorePanel(conflictCount, projectCount));
    }

    public string AskSelection(string title, IEnumerable<string> choices)
    {
        var prompt = new SelectionPrompt<string>()
                .Title($"[deeppink1]{EscapeMarkup(title)}[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to reveal more choices)[/]")
                .HighlightStyle(new Style(SpectrePalette.CyberColors.Secondary))
                .AddChoices(choices);

        return _console.Prompt(prompt);
    }

    public string AskGroupedSelection(string title, Dictionary<string, IEnumerable<string>> groups)
    {
        var prompt = new SelectionPrompt<string>()
                .Title($"[deeppink1]{EscapeMarkup(title)}[/]")
                .PageSize(15)
                .MoreChoicesText("[grey](Move up and down to reveal more choices)[/]")
                .HighlightStyle(new Style(SpectrePalette.CyberColors.Secondary));

        foreach (var group in groups)
        {
            prompt.AddChoiceGroup($"[grey]{group.Key}[/]", group.Value);
        }

        return _console.Prompt(prompt);
    }

    public void WriteStatusDashboard(string directory, List<string> solutions, List<BackupSetInfo> backups, bool isGitRepo, bool hasUnstaged, Dictionary<string, int> targetFrameworks)
    {
        _console.Write(SpectrePanelBuilder.BuildStatusDashboardPanel(directory, solutions, backups, isGitRepo, hasUnstaged, targetFrameworks));
        _console.WriteLine();
    }

    public bool AskConfirmation(string message)
    {
        var selection = _console.Prompt(
            new SelectionPrompt<string>()
                .Title($"[deeppink1]{EscapeMarkup(message)}[/]")
                .AddChoices("Yes", "No")
                .HighlightStyle(new Style(SpectrePalette.CyberColors.Secondary)));

        return selection == "Yes";
    }

    public string AskText(string prompt, string defaultValue = "")
    {
        var textPrompt = new TextPrompt<string>($"[deeppink1]{EscapeMarkup(prompt)}[/]")
            .PromptStyle(new Style(SpectrePalette.CyberColors.Secondary));

        if (!string.IsNullOrEmpty(defaultValue))
        {
            textPrompt.DefaultValue(defaultValue);
        }

        return _console.Prompt(textPrompt);
    }

    public int AskInt(string prompt, int defaultValue)
    {
        var intPrompt = new TextPrompt<int>($"[deeppink1]{EscapeMarkup(prompt)}[/]")
            .PromptStyle(new Style(SpectrePalette.CyberColors.Secondary))
            .DefaultValue(defaultValue);

        return _console.Prompt(intPrompt);
    }

    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath)
    {
        _console.WriteLine();
        _console.Write(SpectreTableBuilder.BuildRollbackPreviewTable(filesToRestore, propsFilePath));
        _console.WriteLine();
    }

    public void WriteAnalysisHeader(int projectCount, int packageCount, int vulnerabilityCount)
    {
        _console.Write(SpectrePanelBuilder.BuildAnalysisHeaderPanel(projectCount, packageCount, vulnerabilityCount));
        _console.WriteLine();
    }

    public void WriteAnalyzerResult(AnalyzerResult result)
    {
        if (result.HasIssues)
        {
            _console.Write(SpectreTableBuilder.BuildAnalyzerResultTable(result));
            _console.WriteLine();
        }
        else
        {
            _console.MarkupLine($"[springgreen1]✔[/] [white]{EscapeMarkup(result.AnalyzerName)}[/] [grey39]- No issues[/]");
        }
    }

    public void WriteAnalysisSummary(AnalysisReport report)
    {
        _console.WriteLine();

        if (report.HasIssues)
        {
            _console.Write(new Rule($"[yellow]ANALYSIS COMPLETE: {report.TotalIssues} ISSUES[/]") { Style = Style.Parse("yellow") });
        }
        else
        {
            _console.Write(new Rule("[springgreen1]ANALYSIS COMPLETE: NO ISSUES[/]") { Style = Style.Parse("springgreen1") });
        }
    }

    private static string EscapeMarkup(string text)
    {
        return Markup.Escape(text);
    }
}