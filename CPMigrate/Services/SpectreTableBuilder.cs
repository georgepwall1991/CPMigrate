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
        Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts,
        ConflictStrategy strategy,
        VersionResolver versionResolver)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(SpectrePalette.CyberColors.Warning)
            .Title("[yellow]VERSION CONFLICTS[/]")
            .AddColumn(new TableColumn("[bold white]PACKAGE[/]"))
            .AddColumn(new TableColumn("[bold white]VERSIONS[/]"))
            .AddColumn(new TableColumn("[bold white]RESOLVED[/]"));

        foreach (var packageName in conflicts)
        {
            var orderedVersions = GetOrderedVersions(packageVersions[packageName]);
            var resolvedVersion = versionResolver.ResolveVersion(packageVersions[packageName], strategy);

            var versionList = string.Join(", ", orderedVersions.Select(v =>
                v == resolvedVersion ? $"[springgreen1]{v}[/]" : $"[grey39]{v}[/]"));

            table.AddRow(
                $"[white]{SpectrePalette.Escape(packageName)}[/]",
                versionList,
                $"[springgreen1]➜ {resolvedVersion}[/]"
            );
        }

        return table;
    }

    public static Tree BuildProjectTree(List<string> projectPaths, string basePath)
    {
        var root = new Tree($"[deeppink1]{SpectrePalette.Escape(Path.GetFileName(basePath))}[/]")
            .Style("grey39")
            .Guide(TreeGuide.Line);

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileName(projectPath);
            root.AddNode($"[springgreen1]{SpectrePalette.Escape(projectName)}[/]");
        }

        return root;
    }

    public static Table BuildRollbackPreviewTable(IEnumerable<string> filesToRestore, string? propsFilePath)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(SpectrePalette.CyberColors.Warning)
            .Title("[yellow]ROLLBACK PREVIEW[/]")
            .AddColumn(new TableColumn("[bold white]ACTION[/]"))
            .AddColumn(new TableColumn("[bold white]FILE[/]"));

        foreach (var file in filesToRestore)
        {
            table.AddRow("[springgreen1]RESTORE[/]", $"[white]{SpectrePalette.Escape(file)}[/]");
        }

        if (!string.IsNullOrEmpty(propsFilePath))
        {
            table.AddRow("[red1]DELETE[/]", $"[white]{SpectrePalette.Escape(propsFilePath)}[/]");
        }

        return table;
    }

    public static Table BuildAnalyzerResultTable(AnalyzerResult result)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(SpectrePalette.CyberColors.Warning)
            .Title($"[yellow]! {SpectrePalette.Escape(result.AnalyzerName)} ({result.Issues.Count})[/]")
            .AddColumn(new TableColumn("[bold white]PACKAGE[/]"))
            .AddColumn(new TableColumn("[bold white]DETAILS[/]"));

        foreach (var issue in result.Issues)
        {
            table.AddRow(
                $"[white]{SpectrePalette.Escape(issue.PackageName)}[/]",
                $"[grey39]{SpectrePalette.Escape(issue.Description)}[/]"
            );
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