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
        AnsiConsole.Clear();
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

        // System Info Bar
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";

        var grid = new Grid();
        grid.AddColumn(new GridColumn().RightAligned());
        grid.AddRow($"[grey39]v{version}[/] [deepskyblue1]•[/] [grey39]{runtime}[/] [deepskyblue1]•[/] [grey39]{os}[/]");
        
        AnsiConsole.Write(grid);
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
                .Select(v => (Original: v, Parsed: NuGet.Versioning.NuGetVersion.TryParse(v, out var parsed) ? parsed : null))
                .ToList();

            var orderedVersions = versions
                .Where(v => v.Parsed != null)
                .OrderByDescending(v => v.Parsed)
                .Select(v => v.Original)
                .Concat(versions.Where(v => v.Parsed == null).Select(v => v.Original))
                .ToList();

            var resolvedVersion = _versionResolver.ResolveVersion(packageVersions[packageName], strategy);

            var versionList = string.Join(", ", orderedVersions.Select(v =>
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

    public void WriteMissionStatus(int step)
    {
        var steps = new[] { "DISCOVERY", "ANALYSIS", "BACKUP", "MIGRATION", "VERIFICATION" };
        var grid = new Grid();
        for (int i = 0; i < steps.Length; i++)
        {
            grid.AddColumn(new GridColumn().Centered());
        }

        var row = new List<string>();
        for (int i = 0; i < steps.Length; i++)
        {
            if (i < step) row.Add($"[springgreen1]✔ {steps[i]}[/]");
            else if (i == step) row.Add($"[deeppink1]▶ {steps[i]}[/]");
            else row.Add($"[grey39]○ {steps[i]}[/]");
        }
        grid.AddRow(row.ToArray());

        AnsiConsole.Write(new Panel(grid) { Border = BoxBorder.None });
        AnsiConsole.WriteLine();
    }

    public void WriteRiskScore(int conflictCount, int projectCount)
    {
        string level, colorMarkup, desc;
        Color color;
        if (conflictCount == 0) { level = "LOW"; colorMarkup = "springgreen1"; color = CyberColors.Success; desc = "Clean migration path."; }
        else if (conflictCount < 5) { level = "MEDIUM"; colorMarkup = "yellow1"; color = CyberColors.Accent; desc = "Minor version divergence detected."; }
        else { level = "HIGH"; colorMarkup = "red1"; color = CyberColors.Error; desc = "Significant version conflicts. Review recommended."; }

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Label");
        table.AddColumn("Value");
        
        table.AddRow("[grey39]Migration Risk:[/]", $"[{colorMarkup} bold]{level}[/]");
        table.AddRow("[grey39]Impact Area:[/]", $"[white]{projectCount} projects[/]");
        table.AddRow("[grey39]Assessment:[/]", $"[grey]{desc}[/]");
        
        AnsiConsole.Write(new Panel(table) 
        { 
            Header = new PanelHeader("[grey] ASSESSMENT [/]"),
            Padding = new Padding(1, 0),
            BorderStyle = new Style(color)
        });
    }

    public string AskSelection(string title, IEnumerable<string> choices)
    {
        var prompt = new SelectionPrompt<string>()
                .Title($"[deeppink1]{EscapeMarkup(title)}[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to reveal more choices)[/]")
                .HighlightStyle(new Style(CyberColors.Secondary))
                .AddChoices(choices);

        return AnsiConsole.Prompt(prompt);
    }

    public void WriteStatusDashboard(string directory, List<string> solutions, List<BackupSetInfo> backups, bool isGitRepo, bool hasUnstaged, Dictionary<string, int> targetFrameworks)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().Padding(2, 0, 0, 0));

        grid.AddRow("[grey39]Directory[/]", $"[white]{EscapeMarkup(directory)}[/]");
        
        var slnStatus = solutions.Count > 0 
            ? $"[springgreen1]{solutions.Count} solution(s) detected[/]" 
            : "[orange1]No solutions found here[/]";
        grid.AddRow("[grey39]Solutions[/]", slnStatus);

        var cpmStatus = File.Exists(Path.Combine(directory, "Directory.Packages.props"))
            ? "[deeppink1]YES[/] [grey](Directory.Packages.props detected)[/]"
            : "[grey39]NO[/]";
        grid.AddRow("[grey39]Using CPM[/]", cpmStatus);

        var gitStatus = !isGitRepo ? "[grey39]Not a Git Repo[/]" 
            : hasUnstaged ? "[orange1]Dirty[/] [grey](Unstaged changes detected)[/]" 
            : "[springgreen1]Clean[/]";
        grid.AddRow("[grey39]Git Status[/]", gitStatus);

        var backupStatus = backups.Count > 0 
            ? $"[cyan1]{backups.Count} backup set(s) available[/]" 
            : "[grey39]None[/]";
        grid.AddRow("[grey39]Backups[/]", backupStatus);

        if (targetFrameworks.Count > 0)
        {
            var tfmList = string.Join(", ", targetFrameworks.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"));
            grid.AddRow("[grey39]Frameworks[/]", $"[yellow1]{EscapeMarkup(tfmList)}[/]");
        }

        var panel = new Panel(grid)
        {
            Header = new PanelHeader("[deeppink1] REPOSITORY CONTEXT [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(CyberColors.Dim),
            Padding = new Padding(1, 0)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public bool AskConfirmation(string message)
    {
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[deeppink1]{EscapeMarkup(message)}[/]")
                .AddChoices(new[] { "Yes", "No" })
                .HighlightStyle(new Style(CyberColors.Secondary)));
        
        return selection == "Yes";
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

    public int AskInt(string prompt, int defaultValue)
    {
        var intPrompt = new TextPrompt<int>($"[deeppink1]{EscapeMarkup(prompt)}[/]")
            .PromptStyle(new Style(CyberColors.Secondary))
            .DefaultValue(defaultValue);

        return AnsiConsole.Prompt(intPrompt);
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

    public void WriteAnalysisHeader(int projectCount, int packageCount, int vulnerabilityCount)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddRow($"[white]Scanning [cyan1]{projectCount}[/] project(s)[/]");
        grid.AddRow($"[white]Found [cyan1]{packageCount}[/] package reference(s)[/]");
        if (vulnerabilityCount > 0)
        {
            grid.AddRow($"[white]Security Audit:[/] [red]{vulnerabilityCount} vulnerabilities found[/]");
        }

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
