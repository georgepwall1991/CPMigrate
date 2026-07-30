using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

internal sealed class DependencyTreeService
{
    private readonly IConsoleService _console;

    public DependencyTreeService(IConsoleService console)
    {
        _console = console;
    }

    public Task<int> RunAsync(ProjectPackageInfo packageInfo)
    {
        _console.WriteHeader();
        _console.Banner("DEPENDENCY TREE");
        _console.WriteLine();

        var projects = packageInfo.References
            .GroupBy(r => r.ProjectPath)
            .OrderBy(g => g.Key);

        foreach (var projectGroup in projects)
        {
            var projectName = Path.GetFileName(projectGroup.Key);
            var packages = projectGroup
                .OrderByDescending(p => p.IsTransitive ? 1 : 0)
                .ThenBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var direct = packages.Where(p => !p.IsTransitive).ToList();
            var transitive = packages.Where(p => p.IsTransitive).ToList();

            var root = new Tree($"[bold {SpectrePalette.Ink.Primary}]{Markup.Escape(projectName)}[/]")
            {
                Guide = TreeGuide.Line,
            };

            if (direct.Count > 0)
            {
                var directNode = root.AddNode($"[{SpectrePalette.Ink.Secondary}]direct ({direct.Count})[/]");
                foreach (var pkg in direct)
                {
                    var versionInk = string.IsNullOrEmpty(pkg.Version) ? SpectrePalette.Ink.Dim : SpectrePalette.Ink.Text;
                    var version = string.IsNullOrEmpty(pkg.Version) ? "(central)" : pkg.Version;
                    directNode.AddNode($"[{SpectrePalette.Ink.Success}]{Markup.Escape(pkg.PackageName)}[/] [{versionInk}]{Markup.Escape(version)}[/]");
                }
            }

            if (transitive.Count > 0)
            {
                var transitiveNode = root.AddNode($"[{SpectrePalette.Ink.Dim}]transitive ({transitive.Count})[/]");
                foreach (var pkg in transitive.Take(20))
                {
                    transitiveNode.AddNode($"[{SpectrePalette.Ink.Muted}]{Markup.Escape(pkg.PackageName)}[/] [{SpectrePalette.Ink.Dim}]{Markup.Escape(pkg.Version)}[/]");
                }

                if (transitive.Count > 20)
                {
                    transitiveNode.AddNode($"[{SpectrePalette.Ink.Dim}]... and {transitive.Count - 20} more[/]");
                }
            }

            if (direct.Count == 0 && transitive.Count == 0)
            {
                root.AddNode($"[{SpectrePalette.Ink.Dim}]no packages[/]");
            }

            AnsiConsole.Write(root);
            AnsiConsole.WriteLine();
        }

        var totalDirect = packageInfo.References.Count(r => !r.IsTransitive);
        var totalTransitive = packageInfo.References.Count(r => r.IsTransitive);
        _console.Dim($"  {projects.Count()} project(s), {totalDirect} direct, {totalTransitive} transitive package(s).");
        _console.WriteLine();

        return Task.FromResult(ExitCodes.Success);
    }
}
