using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

public class SpectreConsoleService : IConsoleService
{
    private readonly VersionResolver _versionResolver;

    // Cyberpunk color palette
    private static class CyberColors
    {
        public static readonly Color Primary = Color.Magenta1;      // Hot pink/magenta
        public static readonly Color Secondary = Color.Cyan1;       // Electric cyan
        public static readonly Color Success = Color.Green1;        // Neon green
        public static readonly Color Warning = Color.Orange1;       // Amber
        public static readonly Color Error = Color.Red1;            // Crimson
        public static readonly Color Dim = Color.Grey;              // Muted
        public static readonly Color Accent = Color.Yellow;         // Highlight
    }

    public SpectreConsoleService(VersionResolver versionResolver)
    {
        _versionResolver = versionResolver;
    }

    public void Info(string message)
    {
        AnsiConsole.MarkupLine($"[grey][[>]][/] [dim]{EscapeMarkup(message)}[/]");
    }

    public void Success(string message)
    {
        AnsiConsole.MarkupLine($"[green1][[▓▓▓]][/] [white]{EscapeMarkup(message)}[/]");
    }

    public void Warning(string message)
    {
        AnsiConsole.MarkupLine($"[orange1][[!]][/] [yellow]░░░ WARNING ░░░[/] [white]{EscapeMarkup(message)}[/]");
    }

    public void Error(string message)
    {
        AnsiConsole.MarkupLine($"[red1][[X]][/] [red]▓▓▓ ERROR ▓▓▓[/] [white]{EscapeMarkup(message)}[/]");
    }

    public void Highlight(string message)
    {
        AnsiConsole.MarkupLine($"[magenta1]>>> {EscapeMarkup(message)} <<<[/]");
    }

    public void Dim(string message)
    {
        AnsiConsole.MarkupLine($"[dim]{EscapeMarkup(message)}[/]");
    }

    public void DryRun(string message)
    {
        AnsiConsole.MarkupLine($"  [cyan1][[◉]][/] [blue]<< SIMULATION >>[/] [grey]{EscapeMarkup(message)}[/]");
    }

    public void WriteHeader()
    {
        // Cyberpunk ASCII art header
        AnsiConsole.MarkupLine("[magenta1] ▄████▄   ██▓███   ███▄ ▄███▓ ██▓  ▄████  ██▀███   ▄▄▄     ▄▄▄█████▓▓█████[/]");
        AnsiConsole.MarkupLine("[magenta1]▒██▀ ▀█  ▓██░  ██▒▓██▒▀█▀ ██▒▓██▒ ██▒ ▀█▒▓██ ▒ ██▒▒████▄   ▓  ██▒ ▓▒▓█   ▀[/]");
        AnsiConsole.MarkupLine("[magenta1]▒▓█    ▄ ▓██░ ██▓▒▓██    ▓██░▒██▒▒██░▄▄▄░▓██ ░▄█ ▒▒██  ▀█▄ ▒ ▓██░ ▒░▒███[/]");
        AnsiConsole.MarkupLine("[magenta1]▒▓▓▄ ▄██▒▒██▄█▓▒ ▒▒██    ▒██ ░██░░▓█  ██▓▒██▀▀█▄  ░██▄▄▄▄██░ ▓██▓ ░ ▒▓█  ▄[/]");
        AnsiConsole.MarkupLine("[magenta1]▒ ▓███▀ ░▒██▒ ░  ░▒██▒   ░██▒░██░░▒▓███▀▒░██▓ ▒██▒ ▓█   ▓██▒ ▒██▒ ░ ░▒████▒[/]");
        AnsiConsole.MarkupLine("[dim]░ ░▒ ▒  ░░▒▒ ░ ░ ░░ ▒░   ░  ░░▓   ░▒   ▒ ░ ▒▓ ░▒▓░ ▒▒   ▓▒█░ ▒ ░░   ░░ ▒░ ░[/]");
        AnsiConsole.MarkupLine("[dim]  ░  ▒   ░░▒ ░    ░  ░      ░ ▒ ░  ░   ░   ░▒ ░ ▒░  ▒   ▒▒ ░   ░     ░ ░  ░[/]");
        AnsiConsole.MarkupLine("[dim]░        ░░       ░      ░    ▒ ░░ ░   ░   ░░   ░   ░   ▒    ░         ░[/]");
        AnsiConsole.MarkupLine("[dim]░ ░               ░      ░    ░        ░    ░           ░  ░           ░  ░[/]");
        AnsiConsole.MarkupLine("[dim]░[/]");
        AnsiConsole.MarkupLine("[dim]═══════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan1]              CENTRAL PACKAGE MANAGEMENT MIGRATION TOOL[/]");
        AnsiConsole.MarkupLine("[dim]                        >>> SYSTEM INITIALIZED <<<[/]");
        AnsiConsole.MarkupLine("[dim]═══════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.WriteLine();
    }

    public void Banner(string message)
    {
        var panel = new Panel(new Markup($"[white]{EscapeMarkup(message)}[/]"))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(CyberColors.Primary),
            Padding = new Padding(2, 0)
        };
        AnsiConsole.Write(panel);
    }

    public void Separator()
    {
        AnsiConsole.MarkupLine("[dim]═══════════════════════════════════════════════════════════════════════════[/]");
    }

    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts, ConflictStrategy strategy)
    {
        var table = new Table()
            .Border(TableBorder.Double)
            .BorderColor(CyberColors.Warning)
            .Title("[yellow]▓▓▓ VERSION CONFLICTS DETECTED ▓▓▓[/]")
            .AddColumn(new TableColumn("[bold white]PACKAGE[/]").Centered())
            .AddColumn(new TableColumn("[bold white]VERSIONS[/]").Centered())
            .AddColumn(new TableColumn("[bold white]RESOLVED[/]").Centered());

        foreach (var packageName in conflicts)
        {
            var versions = packageVersions[packageName]
                .OrderByDescending(v => NuGet.Versioning.NuGetVersion.Parse(v))
                .ToList();
            var resolvedVersion = _versionResolver.ResolveVersion(versions, strategy);

            var versionList = string.Join("\n", versions.Select(v =>
                v == resolvedVersion ? $"[green1]{v}[/]" : $"[dim]{v}[/]"));

            table.AddRow(
                $"[white]{EscapeMarkup(packageName)}[/]",
                versionList,
                $"[green1]>> {resolvedVersion} <<[/]"
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

        if (wasDryRun)
        {
            // Dry-run simulation panel
            var contentLines = new List<string>
            {
                "[cyan1]░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░[/]",
                "[cyan1]░░ SIMULATION COMPLETE ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░[/]",
                "[cyan1]░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░[/]",
                "",
                $"[white]  Projects Scanned        [/][cyan1]>> {projectCount} <<[/]",
                $"[white]  Packages Found          [/][cyan1]>> {packageCount} <<[/]"
            };

            if (conflictCount > 0)
            {
                contentLines.Add($"[white]  Conflicts Detected      [/][yellow]>> {conflictCount} <<[/]");
            }

            contentLines.Add($"[white]  Output File             [/][dim]{EscapeMarkup(propsFilePath)}[/]");
            contentLines.Add("");
            contentLines.Add("[cyan1][[◉]][/] [dim]No changes written to disk[/]");
            contentLines.Add("[cyan1][[◉]][/] [dim]Run without --dry-run to execute[/]");

            var simPanel = new Panel(new Markup(string.Join("\n", contentLines)))
            {
                Border = BoxBorder.Double,
                BorderStyle = new Style(CyberColors.Secondary),
                Padding = new Padding(2, 1)
            };
            AnsiConsole.Write(simPanel);
        }
        else
        {
            // Success panel
            var contentLines = new List<string>
            {
                "[green1]▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄[/]",
                "[green1]██ MIGRATION COMPLETE ██████████████████████████████████████████[/]",
                "[green1]▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀[/]",
                "",
                $"[white]  Projects Processed      [/][green1]>> {projectCount} <<[/]",
                $"[white]  Packages Centralized    [/][green1]>> {packageCount} <<[/]"
            };

            if (conflictCount > 0)
            {
                contentLines.Add($"[white]  Conflicts Resolved      [/][yellow]>> {conflictCount} <<[/]");
            }

            contentLines.Add($"[white]  Output File             [/][cyan1]{EscapeMarkup(propsFilePath)}[/]");

            if (!string.IsNullOrEmpty(backupPath))
            {
                contentLines.Add($"[white]  Backup Location         [/][dim]{EscapeMarkup(backupPath)}[/]");
            }

            contentLines.Add("");
            contentLines.Add("[green1][[▓▓▓]][/] [white]STATUS: ALL SYSTEMS NOMINAL[/]");

            var successPanel = new Panel(new Markup(string.Join("\n", contentLines)))
            {
                Border = BoxBorder.Double,
                BorderStyle = new Style(CyberColors.Success),
                Padding = new Padding(2, 1)
            };
            AnsiConsole.Write(successPanel);
        }
    }

    public void WriteProjectTree(List<string> projectPaths, string basePath)
    {
        AnsiConsole.MarkupLine("[cyan1][[>]] PROJECT SCAN RESULTS[/]");
        AnsiConsole.MarkupLine("[dim]───────────────────────────────────────────[/]");

        var root = new Tree($"[magenta1]{EscapeMarkup(Path.GetFileName(basePath))}[/]")
            .Style("dim");

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileName(projectPath);
            root.AddNode($"[green1]├── {EscapeMarkup(projectName)}[/]");
        }

        AnsiConsole.Write(root);
        AnsiConsole.WriteLine();
    }

    public void WritePropsPreview(string content)
    {
        var panel = new Panel(new Text(content))
        {
            Header = new PanelHeader("[cyan1]Directory.Packages.props[/]"),
            Border = BoxBorder.Double,
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
                .Title($"[magenta1]>>> {EscapeMarkup(title)} <<<[/]")
                .PageSize(10)
                .HighlightStyle(new Style(CyberColors.Secondary))
                .AddChoices(choices));
        return selection;
    }

    public bool AskConfirmation(string message)
    {
        var prompt = new ConfirmationPrompt($"[magenta1]>>> {EscapeMarkup(message)} <<<[/]")
        {
            DefaultValue = false,
            InvalidChoiceMessage = "[red]Invalid input.[/] Please enter [cyan]y[/] or [cyan]n[/]."
        };
        return AnsiConsole.Prompt(prompt);
    }

    public string AskText(string prompt, string defaultValue = "")
    {
        var textPrompt = new TextPrompt<string>($"[magenta1]>>> {EscapeMarkup(prompt)} <<<[/]")
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
            .Border(TableBorder.Double)
            .BorderColor(CyberColors.Warning)
            .Title("[yellow]▓▓▓ ROLLBACK PREVIEW ▓▓▓[/]")
            .AddColumn(new TableColumn("[bold white]ACTION[/]"))
            .AddColumn(new TableColumn("[bold white]FILE[/]"));

        foreach (var file in filesToRestore)
        {
            table.AddRow("[green1]RESTORE[/]", $"[white]{EscapeMarkup(file)}[/]");
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
        var panel = new Panel(new Markup(
            "[magenta1]▓▓▓ ANALYSIS MODE ▓▓▓[/]\n\n" +
            $"[white]Scanning [cyan1]{projectCount}[/] project(s), [cyan1]{packageCount}[/] package reference(s)[/]"))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(CyberColors.Primary),
            Padding = new Padding(2, 1)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public void WriteAnalyzerResult(AnalyzerResult result)
    {
        if (result.HasIssues)
        {
            var table = new Table()
                .Border(TableBorder.Double)
                .BorderColor(CyberColors.Warning)
                .Title($"[yellow][[!]] {EscapeMarkup(result.AnalyzerName)} ({result.Issues.Count} found)[/]")
                .AddColumn(new TableColumn("[bold white]PACKAGE[/]"))
                .AddColumn(new TableColumn("[bold white]DETAILS[/]"));

            foreach (var issue in result.Issues)
            {
                table.AddRow(
                    $"[white]{EscapeMarkup(issue.PackageName)}[/]",
                    $"[dim]{EscapeMarkup(issue.Description)}[/]"
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine($"[green1][[▓▓▓]][/] [white]{EscapeMarkup(result.AnalyzerName)}[/] [dim]- No issues[/]");
        }
    }

    public void WriteAnalysisSummary(AnalysisReport report)
    {
        AnsiConsole.WriteLine();

        if (report.HasIssues)
        {
            AnsiConsole.MarkupLine($"[yellow][[!]] ANALYSIS COMPLETE: {report.TotalIssues} issue(s) detected[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green1][[▓▓▓]] ANALYSIS COMPLETE: ALL SYSTEMS NOMINAL[/]");
        }
    }

    private static string EscapeMarkup(string text)
    {
        return Markup.Escape(text);
    }
}
