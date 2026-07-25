using CPMigrate.Models;
using Spectre.Console;
using Ink = CPMigrate.Services.SpectrePalette.Ink;

namespace CPMigrate.Services;

public class SpectreConsoleService : IConsoleService
{
    private readonly VersionResolver _versionResolver;
    private readonly IAnsiConsole _console;
    private readonly SpectreTheme _theme;

    public SpectreConsoleService(VersionResolver versionResolver, IAnsiConsole? console = null)
    {
        _versionResolver = versionResolver;
        _console = console ?? AnsiConsole.Console;
        _theme = SpectreTheme.For(_console);
    }

    private GlyphSet Glyphs => _theme.Glyphs;

    public bool IsInteractive => _console.Profile.Capabilities.Interactive;

    public void Info(string message)
    {
        _console.MarkupLine($"[{Ink.Dim}]{Glyphs.Info}[/] [dim]{EscapeMarkup(message)}[/]");
    }

    public void Success(string message)
    {
        _console.MarkupLine($"[{Ink.Success}]{Glyphs.Success}[/] [{Ink.Text}]{EscapeMarkup(message)}[/]");
    }

    public void Warning(string message)
    {
        _console.MarkupLine($"[{Ink.Warning}]{Glyphs.Warning}[/] [yellow]{EscapeMarkup(message)}[/]");
    }

    public void Error(string message)
    {
        _console.MarkupLine($"[{Ink.Error}]{Glyphs.Error}[/] [red]{EscapeMarkup(message)}[/]");
    }

    public void Highlight(string message)
    {
        _console.MarkupLine($"[{Ink.Primary}]{Glyphs.Highlight} {EscapeMarkup(message)}[/]");
    }

    public void Dim(string message)
    {
        _console.MarkupLine($"[{Ink.Dim}]{EscapeMarkup(message)}[/]");
    }

    public void DryRun(string message)
    {
        _console.MarkupLine($"  [{Ink.Secondary}]{Glyphs.Pending}[/] [blue]SIMULATION[/] [{Ink.Muted}]{EscapeMarkup(message)}[/]");
    }

    public void WriteHeader()
    {
        _console.Clear();
        _console.WriteLine();

        _console.Write(new FigletText("CPMigrate")
            .LeftJustified()
            .Color(SpectrePalette.CyberColors.Primary));

        var rule = new Rule($"[{Ink.Secondary}]CENTRAL PACKAGE MANAGEMENT MIGRATION TOOL[/]")
        {
            Style = Style.Parse(Ink.Dim),
        };
        _console.Write(rule);

        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";

        var separator = $" [deepskyblue1]{Glyphs.Bullet}[/] ";
        var grid = new Grid();
        grid.AddColumn(new GridColumn().RightAligned());
        grid.AddRow(string.Join(separator,
            $"[bold {Ink.Secondary}]v{version}[/]",
            $"[{Ink.Dim}]{EscapeMarkup(runtime)}[/]",
            $"[{Ink.Dim}]{EscapeMarkup(os)}[/]"));

        _console.Write(grid);
        _console.WriteLine();
    }

    public void Banner(string message)
    {
        _console.Write(SpectrePanelBuilder.BuildBannerPanel(message));
    }

    public void Separator()
    {
        _console.Write(new Rule { Style = Style.Parse(Ink.Dim) });
    }

    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts, ConflictStrategy strategy)
    {
        var table = SpectreTableBuilder.BuildConflictsTable(_theme, packageVersions, conflicts, strategy, _versionResolver);
        _console.WriteLine();
        _console.Write(table);
        _console.WriteLine();
    }

    public void WriteSummaryTable(int projectCount, int packageCount, int conflictCount,
        string propsFilePath, string? backupPath, bool wasDryRun)
    {
        _console.WriteLine();
        _console.Write(SpectrePanelBuilder.BuildSummaryRule(wasDryRun));
        _console.Write(SpectrePanelBuilder.BuildSummaryPanel(_theme, projectCount, packageCount, conflictCount, propsFilePath, backupPath, wasDryRun));
        if (wasDryRun)
        {
            _console.MarkupLine(SpectrePanelBuilder.BuildDryRunHint(_theme));
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
        _console.Write(SpectrePanelBuilder.BuildMissionStatusPanel(_theme, step));
        _console.WriteLine();
    }

    public void WriteRiskScore(int conflictCount, int projectCount)
    {
        _console.Write(SpectrePanelBuilder.BuildRiskScorePanel(_theme, conflictCount, projectCount));
    }

    public string AskSelection(string title, IEnumerable<string> choices)
    {
        var prompt = new SelectionPrompt<string>()
                .Title($"[{Ink.Primary}]{EscapeMarkup(title)}[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to reveal more choices)[/]")
                .HighlightStyle(new Style(SpectrePalette.CyberColors.Secondary))
                .AddChoices(choices);

        return _console.Prompt(prompt);
    }

    public string AskGroupedSelection(string title, Dictionary<string, IEnumerable<string>> groups)
    {
        var prompt = new SelectionPrompt<string>()
                .Title($"[{Ink.Primary}]{EscapeMarkup(title)}[/]")
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
                .Title($"[{Ink.Primary}]{EscapeMarkup(message)}[/]")
                .AddChoices("Yes", "No")
                .HighlightStyle(new Style(SpectrePalette.CyberColors.Secondary)));

        return selection == "Yes";
    }

    public string AskText(string prompt, string defaultValue = "")
    {
        var textPrompt = new TextPrompt<string>($"[{Ink.Primary}]{EscapeMarkup(prompt)}[/]")
            .PromptStyle(new Style(SpectrePalette.CyberColors.Secondary));

        if (!string.IsNullOrEmpty(defaultValue))
        {
            textPrompt.DefaultValue(defaultValue);
        }

        return _console.Prompt(textPrompt);
    }

    public int AskInt(string prompt, int defaultValue)
    {
        var intPrompt = new TextPrompt<int>($"[{Ink.Primary}]{EscapeMarkup(prompt)}[/]")
            .PromptStyle(new Style(SpectrePalette.CyberColors.Secondary))
            .DefaultValue(defaultValue);

        return _console.Prompt(intPrompt);
    }

    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath)
    {
        _console.WriteLine();
        _console.Write(SpectreTableBuilder.BuildRollbackPreviewTable(_theme, filesToRestore, propsFilePath));
        _console.WriteLine();
    }

    public void WriteAnalysisHeader(int projectCount, int packageCount, int vulnerabilityCount)
    {
        _console.Write(SpectrePanelBuilder.BuildAnalysisHeaderPanel(_theme, projectCount, packageCount, vulnerabilityCount));
        _console.WriteLine();
    }

    public void WriteAnalyzerResult(AnalyzerResult result)
    {
        if (result.HasIssues)
        {
            _console.Write(SpectreTableBuilder.BuildAnalyzerResultTable(_theme, result));
            _console.WriteLine();
        }
        else
        {
            _console.MarkupLine($"[{Ink.Success}]{Glyphs.Success}[/] [{Ink.Text}]{EscapeMarkup(result.AnalyzerName)}[/] [{Ink.Dim}]- No issues[/]");
        }
    }

    public void WriteAnalysisSummary(AnalysisReport report)
    {
        _console.WriteLine();

        if (!report.HasIssues)
        {
            _console.Write(new Rule($"[{Ink.Success}]ANALYSIS COMPLETE: NO ISSUES[/]") { Style = Style.Parse(Ink.Success) });
            return;
        }

        _console.Write(new Rule($"[{Ink.Accent}]ANALYSIS COMPLETE: {report.TotalIssues} ISSUES[/]") { Style = Style.Parse(Ink.Accent) });
        _console.WriteLine();
        _console.Write(SpectreTableBuilder.BuildAnalysisBreakdownTable(_theme, report));
    }

    private static string EscapeMarkup(string text)
    {
        return Markup.Escape(text);
    }
}
