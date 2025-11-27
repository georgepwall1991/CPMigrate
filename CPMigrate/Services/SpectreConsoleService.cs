using Spectre.Console;

namespace CPMigrate.Services;

public class SpectreConsoleService : IConsoleService
{
    private readonly VersionResolver _versionResolver;

    public SpectreConsoleService(VersionResolver versionResolver)
    {
        _versionResolver = versionResolver;
    }

    public void Info(string message)
    {
        AnsiConsole.MarkupLine($"[grey]{EscapeMarkup(message)}[/]");
    }

    public void Success(string message)
    {
        AnsiConsole.MarkupLine($"[green]:check_mark: {EscapeMarkup(message)}[/]");
    }

    public void Warning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]:warning: {EscapeMarkup(message)}[/]");
    }

    public void Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]:cross_mark: {EscapeMarkup(message)}[/]");
    }

    public void Highlight(string message)
    {
        AnsiConsole.MarkupLine($"[cyan]{EscapeMarkup(message)}[/]");
    }

    public void Dim(string message)
    {
        AnsiConsole.MarkupLine($"[dim]{EscapeMarkup(message)}[/]");
    }

    public void DryRun(string message)
    {
        AnsiConsole.MarkupLine($"  [blue]:eye: [[DRY-RUN]][/] [grey]{EscapeMarkup(message)}[/]");
    }

    public void WriteHeader()
    {
        var header = new FigletText("CPMigrate")
            .Color(Color.Cyan1)
            .Centered();

        AnsiConsole.Write(header);
        AnsiConsole.MarkupLine("[dim]Central Package Management Migration Tool[/]");
        AnsiConsole.WriteLine();
    }

    public void Banner(string message)
    {
        var panel = new Panel(new Markup($"[white]{EscapeMarkup(message)}[/]"))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(2, 0)
        };
        AnsiConsole.Write(panel);
    }

    public void Separator()
    {
        AnsiConsole.Write(new Rule().RuleStyle("dim"));
    }

    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts, ConflictStrategy strategy)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Title("[yellow]:warning: Version Conflicts Detected[/]")
            .AddColumn(new TableColumn("[bold]Package[/]").Centered())
            .AddColumn(new TableColumn("[bold]Versions Found[/]").Centered())
            .AddColumn(new TableColumn("[bold]Resolved To[/]").Centered());

        foreach (var packageName in conflicts)
        {
            // Using NuGet.Versioning through VersionResolver logic, but we need to expose parsing or replicate sorting
            // Since VersionResolver now uses NuGetVersion internally, we should probably assume the strings are comparable or use the resolver to help.
            // The previous code used VersionResolver.ParseVersion (static) to sort.
            // I removed ParseVersion static method.
            // I should probably add a helper to VersionResolver or just trust string sort? No, versions are semantic.
            // I will rely on the VersionResolver to resolve, but for displaying the list...
            // I can't sort easily without parsing.
            // I'll parse here locally using NuGetVersion since I have the package now.
            
            var versions = packageVersions[packageName].OrderByDescending(v => NuGet.Versioning.NuGetVersion.Parse(v)).ToList();
            var resolvedVersion = _versionResolver.ResolveVersion(versions, strategy);

            var versionList = string.Join("\n", versions.Select(v =>
                v == resolvedVersion ? $"[green]{v}[/]" : $"[dim]{v}[/]"));

            table.AddRow(
                $"[white]{EscapeMarkup(packageName)}[/]",
                versionList,
                $"[green]{resolvedVersion}[/] [dim]({strategy})[/]"
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public void WriteSummaryTable(int projectCount, int packageCount, int conflictCount,
        string propsFilePath, string? backupPath, bool wasDryRun)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(wasDryRun ? Color.Cyan1 : Color.Green)
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Value[/]").RightAligned());

        table.AddRow("Projects processed", $"[white]{projectCount}[/]");
        table.AddRow("Packages centralized", $"[white]{packageCount}[/]");

        if (conflictCount > 0)
        {
            table.AddRow("Conflicts resolved", $"[yellow]{conflictCount}[/]");
        }

        table.AddRow("Output file", $"[cyan]{EscapeMarkup(propsFilePath)}[/]");

        if (!string.IsNullOrEmpty(backupPath))
        {
            table.AddRow("Backup location", $"[dim]{EscapeMarkup(backupPath)}[/]");
        }

        AnsiConsole.WriteLine();

        if (wasDryRun)
        {
            AnsiConsole.MarkupLine("[cyan]:eye: DRY-RUN COMPLETE[/] - [dim]No changes were made[/]");
            AnsiConsole.MarkupLine($"[dim]Run without --dry-run to apply changes[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]:party_popper: Migration completed successfully![/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
    }

    public void WriteProjectTree(List<string> projectPaths, string basePath)
    {
        var root = new Tree($"[cyan]:file_folder: {EscapeMarkup(Path.GetFileName(basePath))}[/]")
            .Style("dim");

        foreach (var projectPath in projectPaths)
        {
            var relativePath = Path.GetRelativePath(basePath, projectPath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);

            // Simplified tree building for now, matching original
            var projectName = Path.GetFileName(projectPath);
            root.AddNode($"[green]:package: {EscapeMarkup(projectName)}[/]");
        }

        AnsiConsole.Write(root);
        AnsiConsole.WriteLine();
    }

    public void WritePropsPreview(string content)
    {
        var panel = new Panel(new Text(content))
        {
            Header = new PanelHeader("[cyan]Directory.Packages.props[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
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
                .Title($"[yellow]{EscapeMarkup(title)}[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(choices));
        return selection;
    }

    public bool AskConfirmation(string message)
    {
        return AnsiConsole.Confirm($"[yellow]{EscapeMarkup(message)}[/]", defaultValue: false);
    }

    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Title("[yellow]:leftwards_arrow_with_hook: Rollback Preview[/]")
            .AddColumn(new TableColumn("[bold]Action[/]"))
            .AddColumn(new TableColumn("[bold]File[/]"));

        foreach (var file in filesToRestore)
        {
            table.AddRow("[green]Restore[/]", $"[white]{EscapeMarkup(file)}[/]");
        }

        if (!string.IsNullOrEmpty(propsFilePath))
        {
            table.AddRow("[red]Delete[/]", $"[white]{EscapeMarkup(propsFilePath)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string EscapeMarkup(string text)
    {
        return Markup.Escape(text);
    }
}
