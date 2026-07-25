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
        return new Panel(new Markup($"[bold {SpectrePalette.Ink.Text}]{SpectrePalette.Escape(message)}[/]"))
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
            Header = new PanelHeader($"[{SpectrePalette.Ink.Secondary}]Directory.Packages.props[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Dim),
            Padding = new Padding(1),
        };
        return panel;
    }

    /// <summary>The label/colour set that distinguishes a dry-run summary from an applied one.</summary>
    private readonly record struct SummaryStyle(
        Color Border, string Header, string ProjectsLabel, string PackagesLabel, string CountInk, string OutputInk)
    {
        public static SummaryStyle DryRun => new(
            SpectrePalette.CyberColors.Secondary,
            $"[{SpectrePalette.Ink.Secondary}]DRY RUN COMPLETE[/]",
            "Projects Scanned",
            "Packages Found",
            SpectrePalette.Ink.Secondary,
            SpectrePalette.Ink.Dim);

        public static SummaryStyle Applied => new(
            SpectrePalette.CyberColors.Success,
            $"[{SpectrePalette.Ink.Success}]SUCCESS[/]",
            "Projects Processed",
            "Packages Centralized",
            SpectrePalette.Ink.Success,
            SpectrePalette.Ink.Secondary);
    }

    /// <summary>
    /// Builds the migration-summary panel. The dry-run and applied variants share a two-column
    /// grid; only the border colour, header, and a few labels differ.
    /// </summary>
    public static Panel BuildSummaryPanel(
        SpectreTheme theme,
        int projectCount, int packageCount, int conflictCount,
        string propsFilePath, string? backupPath, bool wasDryRun)
    {
        var style = wasDryRun ? SummaryStyle.DryRun : SummaryStyle.Applied;
        var bullet = $"[{style.CountInk}]{theme.Glyphs.Bullet}[/]";

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().Padding(2, 0, 0, 0));

        grid.AddRow($"{bullet} [{SpectrePalette.Ink.Text}]{style.ProjectsLabel}[/]", $"[bold {style.CountInk}]{projectCount}[/]");
        grid.AddRow($"{bullet} [{SpectrePalette.Ink.Text}]{style.PackagesLabel}[/]", $"[bold {style.CountInk}]{packageCount}[/]");

        if (conflictCount > 0)
        {
            grid.AddRow(
                $"[{SpectrePalette.Ink.Accent}]{theme.Glyphs.Warning}[/] [{SpectrePalette.Ink.Text}]Conflicts[/]",
                $"[bold {SpectrePalette.Ink.Accent}]{conflictCount}[/] [{SpectrePalette.Ink.Dim}]resolved[/]");
        }

        grid.AddRow(
            $"{bullet} [{SpectrePalette.Ink.Text}]Output File[/]",
            $"[{style.OutputInk}]{SpectrePalette.Escape(propsFilePath)}[/]");

        if (!wasDryRun && !string.IsNullOrEmpty(backupPath))
        {
            grid.AddRow(
                $"{bullet} [{SpectrePalette.Ink.Text}]Backup Location[/]",
                $"[{SpectrePalette.Ink.Dim}]{SpectrePalette.Escape(backupPath)}[/]");
        }

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(style.Border),
            Padding = new Padding(1, 1),
            Header = new PanelHeader(style.Header, Justify.Center),
        };
    }

    /// <summary>The accompanying rule line shown just before the summary panel.</summary>
    public static Rule BuildSummaryRule(bool wasDryRun) =>
        wasDryRun
            ? new Rule($"[{SpectrePalette.Ink.Secondary}]SIMULATION RESULTS[/]") { Style = Style.Parse(SpectrePalette.Ink.Secondary) }
            : new Rule($"[{SpectrePalette.Ink.Success}]MIGRATION RESULTS[/]") { Style = Style.Parse(SpectrePalette.Ink.Success) };

    /// <summary>The trailing hint shown after a dry-run summary.</summary>
    public static string BuildDryRunHint(SpectreTheme theme) =>
        $"\n[{SpectrePalette.Ink.Secondary}]{theme.Glyphs.Info}[/] Run without [{SpectrePalette.Ink.Text}]--dry-run[/] to apply changes";

    public static Panel BuildStatusDashboardPanel(
        string directory, List<string> solutions, List<BackupSetInfo> backups,
        bool isGitRepo, bool hasUnstaged, Dictionary<string, int> targetFrameworks)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().Padding(2, 0, 0, 0));

        grid.AddRow(Label("Directory"), $"[{SpectrePalette.Ink.Text}]{SpectrePalette.Escape(directory)}[/]");
        grid.AddRow(Label("Solutions"), GetSolutionStatus(solutions.Count));
        grid.AddRow(Label("Using CPM"), GetCpmStatus(directory));
        grid.AddRow(Label("Git Status"), GetGitStatus(isGitRepo, hasUnstaged));
        grid.AddRow(Label("Backups"), GetBackupStatus(backups.Count));

        if (targetFrameworks.Count > 0)
        {
            var tfmList = string.Join(", ", targetFrameworks.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"));
            grid.AddRow(Label("Frameworks"), $"[{SpectrePalette.Ink.Accent}]{SpectrePalette.Escape(tfmList)}[/]");
        }

        return new Panel(grid)
        {
            Header = new PanelHeader($"[{SpectrePalette.Ink.Primary}] REPOSITORY CONTEXT [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Dim),
            Padding = new Padding(1, 0),
        };

        static string Label(string text) => $"[{SpectrePalette.Ink.Dim}]{text}[/]";
    }

    public static Panel BuildAnalysisHeaderPanel(SpectreTheme theme, int projectCount, int packageCount, int vulnerabilityCount)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().Padding(2, 0, 0, 0));

        grid.AddRow(
            $"[{SpectrePalette.Ink.Secondary}]{theme.Glyphs.Bullet}[/] [{SpectrePalette.Ink.Text}]Scanning[/]",
            $"[bold {SpectrePalette.Ink.Secondary}]{projectCount}[/] [{SpectrePalette.Ink.Dim}]project(s)[/]");
        grid.AddRow(
            $"[{SpectrePalette.Ink.Secondary}]{theme.Glyphs.Bullet}[/] [{SpectrePalette.Ink.Text}]Found[/]",
            $"[bold {SpectrePalette.Ink.Secondary}]{packageCount}[/] [{SpectrePalette.Ink.Dim}]package reference(s)[/]");

        if (vulnerabilityCount > 0)
        {
            grid.AddRow(
                $"[{SpectrePalette.Ink.Error}]{theme.Glyphs.Error}[/] [{SpectrePalette.Ink.Text}]Security Audit[/]",
                $"[bold {SpectrePalette.Ink.Error}]{vulnerabilityCount}[/] [{SpectrePalette.Ink.Error}]vulnerabilities found[/]");
        }

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Primary),
            Padding = new Padding(1, 1),
            Header = new PanelHeader($"[{SpectrePalette.Ink.Primary}]ANALYSIS MODE[/]", Justify.Center),
        };
    }

    private static readonly string[] MissionSteps =
        ["DISCOVERY", "ANALYSIS", "BACKUP", "MIGRATION", "VERIFICATION"];

    /// <summary>
    /// Renders the pipeline as a connected stepper — completed steps and the rails
    /// behind them light up, the active step is called out, later steps stay dim.
    /// </summary>
    public static Panel BuildMissionStatusPanel(SpectreTheme theme, int step)
    {
        var grid = new Grid();
        var cells = new List<string>();

        for (int i = 0; i < MissionSteps.Length; i++)
        {
            if (i > 0)
            {
                // The rail into a step is "done" only once that step has been reached.
                var railInk = i <= step ? SpectrePalette.Ink.Success : SpectrePalette.Ink.Dim;
                grid.AddColumn(new GridColumn().Centered().NoWrap());
                cells.Add($"[{railInk}]───[/]");
            }

            grid.AddColumn(new GridColumn().Centered().NoWrap());
            cells.Add(RenderStep(theme, MissionSteps[i], i, step));
        }

        grid.AddRow(cells.ToArray());
        return new Panel(grid) { Border = BoxBorder.None };
    }

    private static string RenderStep(SpectreTheme theme, string name, int index, int currentStep)
    {
        if (index < currentStep)
        {
            return $"[{SpectrePalette.Ink.Success}]{theme.Glyphs.Success} {name}[/]";
        }

        if (index == currentStep)
        {
            return $"[bold {SpectrePalette.Ink.Primary}]{theme.Glyphs.Current} {name}[/]";
        }

        return $"[{SpectrePalette.Ink.Dim}]{theme.Glyphs.Pending} {name}[/]";
    }

    /// <summary>The risk band a conflict count falls into, with its presentation.</summary>
    private readonly record struct RiskBand(string Level, string Ink, Color Border, string Description)
    {
        public static RiskBand For(int conflictCount) => conflictCount switch
        {
            0 => new("LOW", SpectrePalette.Ink.Success, SpectrePalette.CyberColors.Success,
                "Clean migration path."),
            < 5 => new("MEDIUM", SpectrePalette.Ink.Accent, SpectrePalette.CyberColors.Accent,
                "Minor version divergence detected."),
            _ => new("HIGH", SpectrePalette.Ink.Error, SpectrePalette.CyberColors.Error,
                "Significant version conflicts. Review recommended."),
        };
    }

    public static Panel BuildRiskScorePanel(SpectreTheme theme, int conflictCount, int projectCount)
    {
        var band = RiskBand.For(conflictCount);

        // Conflicts per project, saturating at one-per-project — enough signal for a meter
        // without pretending to be a calibrated score.
        var fraction = projectCount > 0 ? Math.Min(1d, (double)conflictCount / projectCount) : 0d;
        var score = (int)Math.Round(fraction * 100);

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Label");
        table.AddColumn("Value");

        table.AddRow(
            $"[{SpectrePalette.Ink.Dim}]Migration Risk:[/]",
            $"{SpectrePalette.Meter(fraction, band.Ink, theme.Glyphs)} [bold {band.Ink}]{band.Level}[/] [{SpectrePalette.Ink.Dim}]({score}/100)[/]");
        table.AddRow(
            $"[{SpectrePalette.Ink.Dim}]Impact Area:[/]",
            $"[{SpectrePalette.Ink.Text}]{projectCount} projects[/]" +
            (conflictCount > 0 ? $" [{SpectrePalette.Ink.Dim}]{theme.Glyphs.Bullet} {conflictCount} conflicting package(s)[/]" : string.Empty));
        table.AddRow(
            $"[{SpectrePalette.Ink.Dim}]Assessment:[/]",
            $"[{SpectrePalette.Ink.Muted}]{band.Description}[/]");

        return new Panel(table)
        {
            Header = new PanelHeader($"[{SpectrePalette.Ink.Muted}] ASSESSMENT [/]"),
            Padding = new Padding(1, 0),
            BorderStyle = new Style(band.Border),
        };
    }

    private static string GetSolutionStatus(int count) =>
        count > 0
            ? $"[{SpectrePalette.Ink.Success}]{count} solution(s) detected[/]"
            : $"[{SpectrePalette.Ink.Warning}]No solutions found here[/]";

    private static string GetCpmStatus(string directory) =>
        File.Exists(Path.Combine(directory, "Directory.Packages.props"))
            ? $"[{SpectrePalette.Ink.Primary}]YES[/] [{SpectrePalette.Ink.Muted}](Directory.Packages.props detected)[/]"
            : $"[{SpectrePalette.Ink.Dim}]NO[/]";

    private static string GetGitStatus(bool isGitRepo, bool hasUnstaged)
    {
        if (!isGitRepo)
        {
            return $"[{SpectrePalette.Ink.Dim}]Not a Git Repo[/]";
        }

        return hasUnstaged
            ? $"[{SpectrePalette.Ink.Warning}]Dirty[/] [{SpectrePalette.Ink.Muted}](Unstaged changes detected)[/]"
            : $"[{SpectrePalette.Ink.Success}]Clean[/]";
    }

    private static string GetBackupStatus(int count) =>
        count > 0
            ? $"[{SpectrePalette.Ink.Secondary}]{count} backup set(s) available[/]"
            : $"[{SpectrePalette.Ink.Dim}]None[/]";
}
