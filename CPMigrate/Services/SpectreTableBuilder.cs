using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Builds Spectre.Console table/tree renderables for <see cref="SpectreConsoleService"/>.
/// Pure fabrication — no I/O.
/// </summary>
internal static class SpectreTableBuilder
{
    public static Table BuildConflictsTable(
        SpectreTheme theme,
        Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts,
        ConflictStrategy strategy,
        VersionResolver versionResolver)
    {
        var table = NewTable("VERSION CONFLICTS", "PACKAGE", "VERSIONS", "RESOLVED");

        foreach (var packageName in conflicts)
        {
            var orderedVersions = GetOrderedVersions(packageVersions[packageName]);
            var resolvedVersion = versionResolver.ResolveVersion(packageVersions[packageName], strategy);

            // The winning version stays lit; the ones being dropped fade out, so the
            // resolution decision is readable at a glance rather than by comparing strings.
            var versionList = string.Join($"[{SpectrePalette.Ink.Dim}], [/]", orderedVersions.Select(v =>
                v == resolvedVersion
                    ? $"[bold {SpectrePalette.Ink.Success}]{v}[/]"
                    : $"[{SpectrePalette.Ink.Dim}]{v}[/]"));

            table.AddRow(
                $"[{SpectrePalette.Ink.Text}]{SpectrePalette.Escape(packageName)}[/]",
                versionList,
                $"[{SpectrePalette.Ink.Success}]{theme.Glyphs.Arrow} {resolvedVersion}[/]"
            );
        }

        return table;
    }

    public static Tree BuildProjectTree(List<string> projectPaths, string basePath)
    {
        var root = new Tree($"[bold {SpectrePalette.Ink.Primary}]{SpectrePalette.Escape(Path.GetFileName(basePath))}[/]")
            .Style(SpectrePalette.Ink.Dim)
            .Guide(TreeGuide.Line);

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileName(projectPath);
            root.AddNode($"[{SpectrePalette.Ink.Success}]{SpectrePalette.Escape(projectName)}[/]");
        }

        return root;
    }

    public static Table BuildRollbackPreviewTable(SpectreTheme theme, IEnumerable<string> filesToRestore, string? propsFilePath)
    {
        var table = NewTable("ROLLBACK PREVIEW", "ACTION", "FILE");

        foreach (var file in filesToRestore)
        {
            table.AddRow(
                $"[{SpectrePalette.Ink.Success}]{theme.Glyphs.Arrow} RESTORE[/]",
                $"[{SpectrePalette.Ink.Text}]{SpectrePalette.Escape(file)}[/]");
        }

        if (!string.IsNullOrEmpty(propsFilePath))
        {
            table.AddRow(
                $"[{SpectrePalette.Ink.Error}]{theme.Glyphs.Error} DELETE[/]",
                $"[{SpectrePalette.Ink.Text}]{SpectrePalette.Escape(propsFilePath)}[/]");
        }

        return table;
    }

    public static Table BuildAnalyzerResultTable(SpectreTheme theme, AnalyzerResult result)
    {
        var table = NewTable(
            $"{theme.Glyphs.Warning} {SpectrePalette.Escape(result.AnalyzerName)} ({result.Issues.Count})",
            "PACKAGE", "DETAILS");

        foreach (var issue in result.Issues)
        {
            table.AddRow(
                $"[{SpectrePalette.Ink.Text}]{SpectrePalette.Escape(issue.PackageName)}[/]",
                $"[{SpectrePalette.Ink.Dim}]{SpectrePalette.Escape(issue.Description)}[/]"
            );
        }

        return table;
    }

    /// <summary>
    /// Builds the per-analyzer tally shown once analysis finishes, so a long scroll of
    /// individual tables ends with a single scannable scoreboard.
    /// </summary>
    public static Table BuildAnalysisBreakdownTable(SpectreTheme theme, AnalysisReport report)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(SpectrePalette.CyberColors.Dim)
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]ANALYZER[/]"))
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]ISSUES[/]").RightAligned())
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]SHARE[/]"));

        var worst = report.Results.Count > 0 ? report.Results.Max(r => r.Issues.Count) : 0;

        foreach (var result in report.Results.OrderByDescending(r => r.Issues.Count))
        {
            var clean = result.Issues.Count == 0;
            var ink = clean ? SpectrePalette.Ink.Dim : SpectrePalette.Ink.Accent;
            var icon = clean ? theme.Glyphs.Success : theme.Glyphs.Warning;
            var iconInk = clean ? SpectrePalette.Ink.Success : SpectrePalette.Ink.Accent;

            table.AddRow(
                $"[{iconInk}]{icon}[/] [{ink}]{SpectrePalette.Escape(result.AnalyzerName)}[/]",
                $"[bold {ink}]{result.Issues.Count}[/]",
                SpectrePalette.Meter(worst > 0 ? (double)result.Issues.Count / worst : 0, ink, theme.Glyphs, width: 10));
        }

        return table;
    }

    private static Table NewTable(string title, params string[] columns)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(SpectrePalette.CyberColors.Warning)
            .Title($"[{SpectrePalette.Ink.Accent}]{title}[/]");

        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]{column}[/]"));
        }

        return table;
    }

    /// <summary>
    /// Orders package versions, prioritizing parseable semantic versions in descending order,
    /// followed by unparseable versions.
    /// </summary>
    private static List<string> GetOrderedVersions(HashSet<string> versions)
    {
        var parsed = versions
            .Select(v => (Original: v, Parsed: NuGet.Versioning.NuGetVersion.TryParse(v, out var p) ? p : null))
            .ToList();

        return parsed
            .Where(v => v.Parsed != null)
            .OrderByDescending(v => v.Parsed)
            .Select(v => v.Original)
            .Concat(parsed.Where(v => v.Parsed == null).Select(v => v.Original))
            .ToList();
    }
}
