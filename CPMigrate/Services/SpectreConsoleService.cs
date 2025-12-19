using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

public class SpectreConsoleService : IConsoleService
{
    private readonly VersionResolver _versionResolver;

    // Cyberpunk color palette
    private static class CyberColors
    {
        public static readonly Color Primary = Color.DeepPink1;     // Hot pink
        public static readonly Color Secondary = Color.Cyan1;       // Electric cyan
        public static readonly Color Success = Color.SpringGreen1;  // Neon green
        public static readonly Color Warning = Color.Orange1;       // Amber
        public static readonly Color Error = Color.Red1;            // Crimson
        public static readonly Color Dim = Color.Grey39;            // Muted
        public static readonly Color Accent = Color.Yellow1;        // Highlight
    }

    public SpectreConsoleService(VersionResolver versionResolver)
    {
        _versionResolver = versionResolver;
    }

    public void Info(string message)
    {
        AnsiConsole.MarkupLine($"[grey39]›[/] [dim]{EscapeMarkup(message)}[/]");
    }

    public void Success(string message)
    {
        AnsiConsole.MarkupLine($"[springgreen1]✔[/] [white]{EscapeMarkup(message)}[/]");
    }

    public void Warning(string message)
    {
        AnsiConsole.MarkupLine($"[orange1]![/] [yellow]{EscapeMarkup(message)}[/]");
    }

    public void Error(string message)
    {
        AnsiConsole.MarkupLine($"[red1]✖[/] [red]{EscapeMarkup(message)}[/]");
    }

    public void Highlight(string message)
    {
        AnsiConsole.MarkupLine($"[deeppink1]» {EscapeMarkup(message)}[/]");
    }

    public void Dim(string message)
    {
        AnsiConsole.MarkupLine($"[grey39]{EscapeMarkup(message)}[/]");
    }

    public void DryRun(string message)
    {
        AnsiConsole.MarkupLine($"  [cyan1]○[/] [blue]SIMULATION[/] [grey]{EscapeMarkup(message)}[/]");
    }

    public void WriteHeader()
    {
        AnsiConsole.WriteLine();
        
        // New "Slant" style logo for CPMigrate
        AnsiConsole.Write(new FigletText("CPMigrate")
            .LeftJustified()
            .Color(CyberColors.Primary));

        var rule = new Rule("[cyan1]CENTRAL PACKAGE MANAGEMENT MIGRATION TOOL[/]")
        {
            Style = Style.Parse("grey39")
        };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    public void Banner(string message)
    {
        var panel = new Panel(new Markup($"[bold white]{EscapeMarkup(message)}[/]"))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(CyberColors.Primary),
            Padding = new Padding(2, 0)
        };
        AnsiConsole.Write(panel);
    }

    public void Separator()
    {
        AnsiConsole.Write(new Rule { Style = Style.Parse("grey39") });
    }

    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts, ConflictStrategy strategy)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(CyberColors.Warning)
            .Title("[yellow]VERSION CONFLICTS[/]")
            .AddColumn(new TableColumn("[bold white]PACKAGE[/]"))
            .AddColumn(new TableColumn("[bold white]VERSIONS[/]"))
            .AddColumn(new TableColumn("[bold white]RESOLVED[/]"));

        foreach (var packageName in conflicts)
        {
            var versions = packageVersions[packageName]
                .OrderByDescending(v => NuGet.Versioning.NuGetVersion.Parse(v))
                .ToList();
            var resolvedVersion = _versionResolver.ResolveVersion(versions, strategy);

            var versionList = string.Join(", ", versions.Select(v =>
                v == resolvedVersion ? $"[springgreen1]{v}[/]" : $"[grey39]{v}[/]"));

            table.AddRow(
                $"[white]{EscapeMarkup(packageName)}[/]",
                versionList,
                $"[springgreen1]➜ {resolvedVersion}[/]"
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public void WriteSummaryTable(int projectCount, int packageCount, int conflictCount,
        string propsFilePath, string? backupPath, bool wasDryRun)
    {
        AnsiConsole.WriteLine();

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        if (wasDryRun)
        {
            // Dry-run layout
            AnsiConsole.Write(new Rule("[cyan1]SIMULATION RESULTS[/]") { Style = Style.Parse("cyan1") });
            
            grid.AddRow("[white]Projects Scanned[/]", $"[cyan1]{projectCount}[/]");
            grid.AddRow("[white]Packages Found[/]", $"[cyan1]{packageCount}[/]");
            
            if (conflictCount > 0)
            {
                grid.AddRow("[white]Conflicts Detected[/]", $"[yellow]{conflictCount}[/]");
            }

            grid.AddRow("[white]Output File[/]", $"[grey39]{EscapeMarkup(propsFilePath)}[/]");
            
            var panel = new Panel(grid)
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(CyberColors.Secondary),
                Padding = new Padding(1, 1),
                Header = new PanelHeader("[cyan1]DRY RUN COMPLETE[/]", Justify.Center)
            };
            AnsiConsole.Write(panel);
            
            AnsiConsole.MarkupLine("\n[cyan1]ℹ[/] Run without [white]--dry-run[/] to apply changes");
        }
        else
        {
            // Success layout
            AnsiConsole.Write(new Rule("[springgreen1]MIGRATION RESULTS[/]") { Style = Style.Parse("springgreen1") });

            grid.AddRow("[white]Projects Processed[/]", $"[springgreen1]{projectCount}[/]");
            grid.AddRow("[white]Packages Centralized[/]", $"[springgreen1]{packageCount}[/]");

            if (conflictCount > 0)
            {
                grid.AddRow("[white]Conflicts Resolved[/]", $"[yellow]{conflictCount}[/]");
            }

            grid.AddRow("[white]Output File[/]", $"[cyan1]{EscapeMarkup(propsFilePath)}[/]");

            if (!string.IsNullOrEmpty(backupPath))
            {
                grid.AddRow("[white]Backup Location[/]", $"[grey39]{EscapeMarkup(backupPath)}[/]");
            }

            var panel = new Panel(grid)
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(CyberColors.Success),
                Padding = new Padding(1, 1),
                Header = new PanelHeader("[springgreen1]SUCCESS[/]", Justify.Center)
            };
            AnsiConsole.Write(panel);
        }
    }

    public void WriteProjectTree(List<string> projectPaths, string basePath)
    {
        var root = new Tree($"[deeppink1]{EscapeMarkup(Path.GetFileName(basePath))}[/]")
            .Style("grey39")
            .Guide(TreeGuide.Line);

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileName(projectPath);
            root.AddNode($"[springgreen1]{EscapeMarkup(projectName)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(root);
        AnsiConsole.WriteLine();
    }

    public void WritePropsPreview(string content)
    {
        var panel = new Panel(new Text(content))
        {
            Header = new PanelHeader("[cyan1]Directory.Packages.props[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(CyberColors.Dim),
            Padding = new Padding(1)
        };

        AnsiConsole.Write(panel);
    }

    public void WriteMarkup(string message)
    {
        AnsiConsole.MarkupLine(message);
    }

    public void WriteLine(string message = "")
    {
        AnsiConsole.WriteLine(message);
    }

    public string AskSelection(string title, IEnumerable<string> choices)
    {
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[deeppink1]{EscapeMarkup(title)}[/]")
                .PageSize(10)
                .HighlightStyle(new Style(CyberColors.Secondary))
                .AddChoices(choices));
        return selection;
    }

    public bool AskConfirmation(string message)
    {
        var prompt = new ConfirmationPrompt($"[deeppink1]{EscapeMarkup(message)}[/]")
        {
            DefaultValue = false,
            InvalidChoiceMessage = "[red]Invalid input.[/] Please enter [cyan]y[/] or [cyan]n[/]."
        };
        return AnsiConsole.Prompt(prompt);
    }

    public string AskText(string prompt, string defaultValue = "")
    {
        var textPrompt = new TextPrompt<string>($"[deeppink1]{EscapeMarkup(prompt)}[/]")
            .PromptStyle(new Style(CyberColors.Secondary));

        if (!string.IsNullOrEmpty(defaultValue))
        {
            textPrompt.DefaultValue(defaultValue);
        }

        return AnsiConsole.Prompt(textPrompt);
    }

    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(CyberColors.Warning)
            .Title("[yellow]ROLLBACK PREVIEW[/]")
            .AddColumn(new TableColumn("[bold white]ACTION[/]"))
            .AddColumn(new TableColumn("[bold white]FILE[/]"));

        foreach (var file in filesToRestore)
        {
            table.AddRow("[springgreen1]RESTORE[/]", $"[white]{EscapeMarkup(file)}[/]");
        }

        if (!string.IsNullOrEmpty(propsFilePath))
        {
            table.AddRow("[red1]DELETE[/]", $"[white]{EscapeMarkup(propsFilePath)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public void WriteAnalysisHeader(int projectCount, int packageCount)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddRow($"[white]Scanning [cyan1]{projectCount}[/] project(s)[/]");
        grid.AddRow($"[white]Found [cyan1]{packageCount}[/] package reference(s)[/]");

        var panel = new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(CyberColors.Primary),
            Padding = new Padding(1, 1),
            Header = new PanelHeader("[deeppink1]ANALYSIS MODE[/]", Justify.Center)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public void WriteAnalyzerResult(AnalyzerResult result)
    {
        if (result.HasIssues)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(CyberColors.Warning)
                .Title($"[yellow]! {EscapeMarkup(result.AnalyzerName)} ({result.Issues.Count})[/]")
                .AddColumn(new TableColumn("[bold white]PACKAGE[/]"))
                .AddColumn(new TableColumn("[bold white]DETAILS[/]"));

            foreach (var issue in result.Issues)
            {
                table.AddRow(
                    $"[white]{EscapeMarkup(issue.PackageName)}[/]",
                    $"[grey39]{EscapeMarkup(issue.Description)}[/]"
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine($"[springgreen1]✔[/] [white]{EscapeMarkup(result.AnalyzerName)}[/] [grey39]- No issues[/]");
        }
    }

    public void WriteAnalysisSummary(AnalysisReport report)
    {
        AnsiConsole.WriteLine();

        if (report.HasIssues)
        {
            AnsiConsole.Write(new Rule($"[yellow]ANALYSIS COMPLETE: {report.TotalIssues} ISSUES[/]") { Style = Style.Parse("yellow") });
        }
        else
        {
            AnsiConsole.Write(new Rule("[springgreen1]ANALYSIS COMPLETE: NO ISSUES[/]") { Style = Style.Parse("springgreen1") });
        }
    }

    private static string EscapeMarkup(string text)
    {
        return Markup.Escape(text);
    }
}
