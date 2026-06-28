using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Builds Spectre.Console panel/grid renderables for the dashboard surfaces of
/// <see cref="SpectreConsoleService"/>. Pure fabrication — no I/O.
/// </summary>
internal static class SpectrePanelBuilder
{
    public static Panel BuildBannerPanel(string message)
    {
        return new Panel(new Markup($"[bold white]{SpectrePalette.Escape(message)}[/]"))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Primary),
            Padding = new Padding(2, 0),
        };
    }

    public static Panel BuildPropsPreviewPanel(string content)
    {
        var panel = new Panel(new Text(content))
        {
            Header = new PanelHeader("[cyan1]Directory.Packages.props[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Dim),
            Padding = new Padding(1),
        };
        return panel;
    }

    /// <summary>
    /// Builds the migration-summary panel. The dry-run and success variants share a two-column
    /// grid; only the rule label, border color, header, and a few rows differ.
    /// </summary>
    public static Panel BuildSummaryPanel(
        int projectCount, int packageCount, int conflictCount,
        string propsFilePath, string? backupPath, bool wasDryRun)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        Color borderColor;
        string headerMarkup;
        string projectsLabel;
        string packagesLabel;
        string projectsColor;
        string packagesColor;
        string outputLabel;
        string outputColor;

        if (wasDryRun)
        {
            borderColor = SpectrePalette.CyberColors.Secondary;
            headerMarkup = "[cyan1]DRY RUN COMPLETE[/]";
            projectsLabel = "Projects Scanned";
            packagesLabel = "Packages Found";
            projectsColor = "cyan1";
            packagesColor = "cyan1";
            outputLabel = "Output File";
            outputColor = "grey39";
        }
        else
        {
            borderColor = SpectrePalette.CyberColors.Success;
            headerMarkup = "[springgreen1]SUCCESS[/]";
            projectsLabel = "Projects Processed";
            packagesLabel = "Packages Centralized";
            projectsColor = "springgreen1";
            packagesColor = "springgreen1";
            outputLabel = "Output File";
            outputColor = "cyan1";
        }

        grid.AddRow($"[white]{projectsLabel}[/]", $"[{projectsColor}]{projectCount}[/]");
        grid.AddRow($"[white]{packagesLabel}[/]", $"[{packagesColor}]{packageCount}[/]");

        if (conflictCount > 0)
        {
            grid.AddRow("[white]Conflicts[/]", $"[yellow]{conflictCount}[/]");
        }

        grid.AddRow($"[white]{outputLabel}[/]", $"[{outputColor}]{SpectrePalette.Escape(propsFilePath)}[/]");

        if (!wasDryRun && !string.IsNullOrEmpty(backupPath))
        {
            grid.AddRow("[white]Backup Location[/]", $"[grey39]{SpectrePalette.Escape(backupPath)}[/]");
        }

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(borderColor),
            Padding = new Padding(1, 1),
            Header = new PanelHeader(headerMarkup, Justify.Center),
        };
    }

    /// <summary>The accompanying rule line shown just before the summary panel.</summary>
    public static Rule BuildSummaryRule(bool wasDryRun) =>
        wasDryRun
            ? new Rule("[cyan1]SIMULATION RESULTS[/]") { Style = Style.Parse("cyan1") }
            : new Rule("[springgreen1]MIGRATION RESULTS[/]") { Style = Style.Parse("springgreen1") };

    /// <summary>The trailing hint shown after a dry-run summary.</summary>
    public const string DryRunHint = "\n[cyan1]ℹ[/] Run without [white]--dry-run[/] to apply changes";

    public static Panel BuildStatusDashboardPanel(
        string directory, List<string> solutions, List<BackupSetInfo> backups,
        bool isGitRepo, bool hasUnstaged, Dictionary<string, int> targetFrameworks)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().Padding(2, 0, 0, 0));

        grid.AddRow("[grey39]Directory[/]", $"[white]{SpectrePalette.Escape(directory)}[/]");
        grid.AddRow("[grey39]Solutions[/]", GetSolutionStatus(solutions.Count));
        grid.AddRow("[grey39]Using CPM[/]", GetCpmStatus(directory));
        grid.AddRow("[grey39]Git Status[/]", GetGitStatus(isGitRepo, hasUnstaged));
        grid.AddRow("[grey39]Backups[/]", GetBackupStatus(backups.Count));

        if (targetFrameworks.Count > 0)
        {
            var tfmList = string.Join(", ", targetFrameworks.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"));
            grid.AddRow("[grey39]Frameworks[/]", $"[yellow1]{SpectrePalette.Escape(tfmList)}[/]");
        }

        return new Panel(grid)
        {
            Header = new PanelHeader("[deeppink1] REPOSITORY CONTEXT [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Dim),
            Padding = new Padding(1, 0),
        };
    }

    public static Panel BuildAnalysisHeaderPanel(int projectCount, int packageCount, int vulnerabilityCount)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddRow($"[white]Scanning [cyan1]{projectCount}[/] project(s)[/]");
        grid.AddRow($"[white]Found [cyan1]{packageCount}[/] package reference(s)[/]");
        if (vulnerabilityCount > 0)
        {
            grid.AddRow($"[white]Security Audit:[/] [red]{vulnerabilityCount} vulnerabilities found[/]");
        }

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Primary),
            Padding = new Padding(1, 1),
            Header = new PanelHeader("[deeppink1]ANALYSIS MODE[/]", Justify.Center),
        };
    }

    public static Panel BuildMissionStatusPanel(int step)
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
            if (i < step)
            {
                row.Add($"[springgreen1]✔ {steps[i]}[/]");
            }
            else if (i == step)
            {
                row.Add($"[deeppink1]▶ {steps[i]}[/]");
            }
            else
            {
                row.Add($"[grey39]○ {steps[i]}[/]");
            }
        }
        grid.AddRow(row.ToArray());

        return new Panel(grid) { Border = BoxBorder.None };
    }

    public static Panel BuildRiskScorePanel(int conflictCount, int projectCount)
    {
        string level, colorMarkup, desc;
        Color color;
        if (conflictCount == 0)
        {
            (level, colorMarkup, color, desc) =
                ("LOW", "springgreen1", SpectrePalette.CyberColors.Success, "Clean migration path.");
        }
        else if (conflictCount < 5)
        {
            (level, colorMarkup, color, desc) =
                ("MEDIUM", "yellow1", SpectrePalette.CyberColors.Accent, "Minor version divergence detected.");
        }
        else
        {
            (level, colorMarkup, color, desc) =
                ("HIGH", "red1", SpectrePalette.CyberColors.Error, "Significant version conflicts. Review recommended.");
        }

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Label");
        table.AddColumn("Value");

        table.AddRow("[grey39]Migration Risk:[/]", $"[{colorMarkup} bold]{level}[/]");
        table.AddRow("[grey39]Impact Area:[/]", $"[white]{projectCount} projects[/]");
        table.AddRow("[grey39]Assessment:[/]", $"[grey]{desc}[/]");

        return new Panel(table)
        {
            Header = new PanelHeader("[grey] ASSESSMENT [/]"),
            Padding = new Padding(1, 0),
            BorderStyle = new Style(color),
        };
    }

    private static string GetSolutionStatus(int count) =>
        count > 0
            ? $"[springgreen1]{count} solution(s) detected[/]"
            : "[orange1]No solutions found here[/]";

    private static string GetCpmStatus(string directory) =>
        File.Exists(Path.Combine(directory, "Directory.Packages.props"))
            ? "[deeppink1]YES[/] [grey](Directory.Packages.props detected)[/]"
            : "[grey39]NO[/]";

    private static string GetGitStatus(bool isGitRepo, bool hasUnstaged)
    {
        if (!isGitRepo)
        {
            return "[grey39]Not a Git Repo[/]";
        }

        return hasUnstaged
            ? "[orange1]Dirty[/] [grey](Unstaged changes detected)[/]"
            : "[springgreen1]Clean[/]";
    }

    private static string GetBackupStatus(int count) =>
        count > 0
            ? $"[cyan1]{count} backup set(s) available[/]"
            : "[grey39]None[/]";
}