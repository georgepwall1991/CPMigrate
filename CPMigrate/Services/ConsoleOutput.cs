using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Provides rich console output formatting using Spectre.Console.
/// </summary>
public static class ConsoleOutput
{
    /// <summary>
    /// Writes an informational message.
    /// </summary>
    public static void Info(string message)
    {
        AnsiConsole.MarkupLine($"[grey]{EscapeMarkup(message)}[/]");
    }

    /// <summary>
    /// Writes a success message with a checkmark.
    /// </summary>
    public static void Success(string message)
    {
        AnsiConsole.MarkupLine($"[green]:check_mark: {EscapeMarkup(message)}[/]");
    }

    /// <summary>
    /// Writes a warning message with a warning icon.
    /// </summary>
    public static void Warning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]:warning: {EscapeMarkup(message)}[/]");
    }

    /// <summary>
    /// Writes an error message to stderr with an X icon.
    /// </summary>
    public static void Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]:cross_mark: {EscapeMarkup(message)}[/]");
    }

    /// <summary>
    /// Writes a highlighted message in cyan.
    /// </summary>
    public static void Highlight(string message)
    {
        AnsiConsole.MarkupLine($"[cyan]{EscapeMarkup(message)}[/]");
    }

    /// <summary>
    /// Writes a dim/muted message.
    /// </summary>
    public static void Dim(string message)
    {
        AnsiConsole.MarkupLine($"[dim]{EscapeMarkup(message)}[/]");
    }

    /// <summary>
    /// Writes a dry-run prefix message with an eye icon.
    /// </summary>
    public static void DryRun(string message)
    {
        AnsiConsole.MarkupLine($"  [blue]:eye: [[DRY-RUN]][/] [grey]{EscapeMarkup(message)}[/]");
    }

    /// <summary>
    /// Displays the application header with ASCII art.
    /// </summary>
    public static void WriteHeader()
    {
        var header = new FigletText("CPMigrate")
            .Color(Color.Cyan1)
            .Centered();

        AnsiConsole.Write(header);
        AnsiConsole.MarkupLine("[dim]Central Package Management Migration Tool[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Writes a banner for major mode indicators (like dry-run).
    /// </summary>
    public static void Banner(string message)
    {
        var panel = new Panel(new Markup($"[white]{EscapeMarkup(message)}[/]"))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(2, 0)
        };
        AnsiConsole.Write(panel);
    }

    /// <summary>
    /// Writes a separator line.
    /// </summary>
    public static void Separator()
    {
        AnsiConsole.Write(new Rule().RuleStyle("dim"));
    }

    /// <summary>
    /// Displays a table of version conflicts.
    /// </summary>
    public static void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts, ConflictStrategy strategy)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Title("[yellow]:warning: Version Conflicts Detected[/]")
            .AddColumn(new TableColumn("[bold]Package[/]").Centered())
            .AddColumn(new TableColumn("[bold]Versions Found[/]").Centered())
            .AddColumn(new TableColumn("[bold]Resolved To[/]").Centered());

        var resolver = new VersionResolver();

        foreach (var packageName in conflicts)
        {
            var versions = packageVersions[packageName].OrderByDescending(VersionResolver.ParseVersion).ToList();
            var resolvedVersion = resolver.ResolveVersion(versions, strategy);

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

    /// <summary>
    /// Displays a summary table after migration.
    /// </summary>
    public static void WriteSummaryTable(int projectCount, int packageCount, int conflictCount,
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

    /// <summary>
    /// Displays found projects in a tree structure.
    /// </summary>
    public static void WriteProjectTree(List<string> projectPaths, string basePath)
    {
        var root = new Tree($"[cyan]:file_folder: {EscapeMarkup(Path.GetFileName(basePath))}[/]")
            .Style("dim");

        foreach (var projectPath in projectPaths)
        {
            var relativePath = Path.GetRelativePath(basePath, projectPath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);

            var currentNode = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                // Add folder nodes (simplified - just shows project files)
            }

            var projectName = Path.GetFileName(projectPath);
            root.AddNode($"[green]:package: {EscapeMarkup(projectName)}[/]");
        }

        AnsiConsole.Write(root);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Displays the generated props file content with syntax highlighting.
    /// </summary>
    public static void WritePropsPreview(string content)
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

    /// <summary>
    /// Escapes special Spectre.Console markup characters.
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return Markup.Escape(text);
    }
}
