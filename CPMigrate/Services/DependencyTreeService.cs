using CPMigrate.Models;
using NuGet.Versioning;
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
            var root = BuildProjectTree(projectName, projectGroup.ToList());

            AnsiConsole.Write(root);
            AnsiConsole.WriteLine();
        }

        var totalDirect = CountDistinctPackages(packageInfo.References, transitive: false);
        var totalTransitive = CountDistinctPackages(packageInfo.References, transitive: true);
        _console.Dim($"  {projects.Count()} project(s), {totalDirect} direct, {totalTransitive} transitive package(s).");
        _console.WriteLine();

        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>
    /// Builds one project's package tree: direct packages first in case-insensitive name order,
    /// then transitive capped at 20 with an overflow remainder. Pure, so the shape is testable
    /// without a console; the caller owns rendering.
    ///
    /// The resolved scan lists a package once per target framework, so the rows are grouped by
    /// package name here — a multi-targeted project would otherwise show every package once per
    /// TFM. When frameworks resolve different versions (a conditional pin or per-TFM
    /// VersionOverride), every distinct version is shown rather than silently picking one: that
    /// split is exactly the kind of finding a dependency tree exists to surface.
    /// </summary>
    internal static Tree BuildProjectTree(string projectName, IReadOnlyList<PackageReference> references)
    {
        var direct = GroupByPackage(references.Where(p => !p.IsTransitive));
        var transitive = GroupByPackage(references.Where(p => p.IsTransitive));

        var root = new Tree($"[bold {SpectrePalette.Ink.Primary}]{Markup.Escape(projectName)}[/]")
        {
            Guide = TreeGuide.Line,
        };

        if (direct.Count > 0)
        {
            var directNode = root.AddNode($"[{SpectrePalette.Ink.Secondary}]direct ({direct.Count})[/]");
            foreach (var (name, versions) in direct)
            {
                var version = string.Join(", ", versions);
                var versionInk = string.IsNullOrEmpty(version) ? SpectrePalette.Ink.Dim : SpectrePalette.Ink.Text;
                directNode.AddNode($"[{SpectrePalette.Ink.Success}]{Markup.Escape(name)}[/] [{versionInk}]{Markup.Escape(string.IsNullOrEmpty(version) ? "(central)" : version)}[/]");
            }
        }

        if (transitive.Count > 0)
        {
            var transitiveNode = root.AddNode($"[{SpectrePalette.Ink.Dim}]transitive ({transitive.Count})[/]");
            foreach (var (name, versions) in transitive.Take(20))
            {
                transitiveNode.AddNode($"[{SpectrePalette.Ink.Muted}]{Markup.Escape(name)}[/] [{SpectrePalette.Ink.Dim}]{Markup.Escape(string.Join(", ", versions))}[/]");
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

        return root;
    }

    /// <summary>
    /// Collapses per-framework rows into one entry per package, carrying its distinct resolved
    /// versions in ascending order. Versions that do not parse as NuGet versions sort after ones
    /// that do, in ordinal order — a resolved version that will not parse is already unusual, and
    /// a stable order matters more than a clever one.
    /// </summary>
    private static IReadOnlyList<(string Name, IReadOnlyList<string> Versions)> GroupByPackage(
        IEnumerable<PackageReference> references
    )
    {
        return references
            .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Versions: (IReadOnlyList<string>)[.. g
                .Select(r => r.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => NuGetVersion.TryParse(v, out _) ? 0 : 1)
                .ThenBy(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
                .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)]))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CountDistinctPackages(
        IReadOnlyList<PackageReference> references,
        bool transitive
    )
    {
        return references
            .Where(r => r.IsTransitive == transitive)
            .DistinctBy(r => (r.ProjectPath, r.PackageName.ToUpperInvariant()))
            .Count();
    }
}
