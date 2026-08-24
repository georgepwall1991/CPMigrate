using CPMigrate.Models;
using NuGet.Versioning;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// What the <c>--why</c> scan gathered about one package across a workspace, before rendering.
///
/// <para>
/// <see cref="PackageOriginService.Analyze"/> turns this into a
/// <see cref="PackageOriginReport"/>; keeping the input as data rather than service calls is what
/// makes the analysis testable without a solution on disk.
/// </para>
/// </summary>
/// <param name="PackageId">The package whose origin was asked about, verbatim as typed.</param>
/// <param name="Packages">
/// Resolved references (direct and transitive) plus the declarations read from project files.
/// </param>
/// <param name="ResolvedGraphs">
/// Per-project resolved graphs with dependency edges, used to name which direct package pulls the
/// target in transitively. Projects without a readable graph simply have no introducer names.
/// </param>
/// <param name="ProjectCount">How many projects the workspace discovery found.</param>
/// <param name="FailedScanCount">How many of those could not be scanned at all.</param>
public sealed record PackageOriginRequest(
    string PackageId,
    ProjectPackageInfo Packages,
    IReadOnlyList<ProjectResolvedGraph> ResolvedGraphs,
    int ProjectCount,
    int FailedScanCount
);

/// <summary>
/// How one project relates to the traced package, strongest relationship first: an Include beats an
/// Update-only amendment, and either beats a transitive sighting.
/// </summary>
public enum PackageOriginKind
{
    /// <summary>The project does not reference or see the package at all.</summary>
    NotPresent,

    /// <summary>The package appears only in the resolved graph — some direct dependency pulls it in.</summary>
    TransitiveOnly,

    /// <summary>
    /// The project only amends an inherited reference via <c>PackageReference Update</c>; it does not
    /// bring the package into the graph itself.
    /// </summary>
    UpdateOnly,

    /// <summary>The project declares the package with an <c>Include</c> and no version of its own.</summary>
    CentralPin,

    /// <summary>The project declares the package with an inline version (or a VersionOverride).</summary>
    InlineVersion,
}

/// <summary>One project's relationship to the traced package.</summary>
/// <param name="ProjectPath">Full path to the project file.</param>
/// <param name="ProjectName">File name of the project, for display.</param>
/// <param name="Kind">The strongest relationship found.</param>
/// <param name="InlineVersion">
/// The version the project pins inline when <paramref name="Kind"/> is <see cref="PackageOriginKind.InlineVersion"/>,
/// preferring <c>VersionOverride</c> — under CPM that is the version actually in force.
/// </param>
/// <param name="ResolvedVersion">
/// The version NuGet resolved for this project, normalized for comparison; null when the package
/// does not reach this project.
/// </param>
/// <param name="TransitiveIntroducers">
/// Direct packages that pull the target in, when <paramref name="Kind"/> is
/// <see cref="PackageOriginKind.TransitiveOnly"/>. Empty when no resolved graph could say.
/// </param>
public sealed record PackageOriginProjectReport(
    string ProjectPath,
    string ProjectName,
    PackageOriginKind Kind,
    string? InlineVersion = null,
    string? ResolvedVersion = null,
    IReadOnlyList<string>? TransitiveIntroducers = null
);

/// <summary>One distinct version of the traced package, and who resolves to it.</summary>
public sealed record PackageOriginVersionUsage(
    string Version,
    IReadOnlyList<string> ProjectNames
);

/// <summary>The answer to a <c>--why</c> question, ready to render.</summary>
/// <param name="PackageId">The package asked about.</param>
/// <param name="Found">Whether any project in the workspace declares or sees the package.</param>
/// <param name="Projects">Per-project findings, ordered by path.</param>
/// <param name="VersionsInUse">
/// Every distinct resolved version and the projects using it. More than one entry is version drift.
/// </param>
/// <param name="Suggestions">
/// Near-miss package names present in the workspace, when <paramref name="Found"/> is false.
/// </param>
public sealed record PackageOriginReport(
    string PackageId,
    bool Found,
    IReadOnlyList<PackageOriginProjectReport> Projects,
    IReadOnlyList<PackageOriginVersionUsage> VersionsInUse,
    IReadOnlyList<string> Suggestions
);

/// <summary>
/// Answers "why do I have this package?" — which projects declare it directly, which only inherit
/// it transitively and through what, and whether the versions agree across the workspace.
///
/// <para>
/// Rendering mirrors <see cref="DependencyTreeService"/>: one tree per subject, palette ink only,
/// and everything user-derived escaped before it reaches markup.
/// </para>
/// </summary>
internal sealed class PackageOriginService
{
    private readonly IConsoleService _console;

    public PackageOriginService(IConsoleService console)
    {
        _console = console;
    }

    /// <summary>
    /// Analyzes, renders, and maps the outcome onto exit codes: success when answered, a validation
    /// error when the package is not in the workspace at all, and incomplete-analysis when part of
    /// the workspace went unexamined — a diagnostic must not read as complete when it is not.
    /// </summary>
    public Task<int> RunAsync(PackageOriginRequest request)
    {
        _console.WriteHeader();
        _console.Banner("PACKAGE ORIGIN");
        _console.WriteLine();

        var report = Analyze(request);

        if (!report.Found)
        {
            _console.Error(
                $"Package '{request.PackageId}' was not declared or resolved by any scanned project."
            );
            foreach (var suggestion in report.Suggestions)
            {
                _console.Dim($"  Did you mean '{suggestion}'?");
            }

            if (report.Suggestions.Count == 0)
            {
                _console.Dim("Run --tree --transitive to list every package in the workspace.");
            }

            return Task.FromResult(ExitCodes.ValidationError);
        }

        if (request.FailedScanCount > 0)
        {
            _console.Warning(
                $"Could not scan {request.FailedScanCount} of {request.ProjectCount} project(s); "
                    + "findings below cover only what could be read."
            );
        }

        Render(request.PackageId, report);

        return Task.FromResult(
            request.FailedScanCount > 0 ? ExitCodes.IncompleteAnalysis : ExitCodes.Success
        );
    }

    /// <summary>
    /// Classifies every scanned project against the package and settles the drift verdict.
    ///
    /// Versions are compared normalized ("4.3" and "4.3.0" are the same version), because
    /// manufacturing drift out of two spellings of one release would teach nobody anything.
    /// </summary>
    internal static PackageOriginReport Analyze(PackageOriginRequest request)
    {
        var graphsByProject = request.ResolvedGraphs
            .GroupBy(g => g.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        List<PackageOriginProjectReport> projects = [];
        foreach (
            var projectPath in request
                .Packages.References.Select(r => r.ProjectPath)
                .Concat(request.Packages.GetDeclaredReferences().Select(r => r.ProjectPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        )
        {
            projects.Add(ClassifyProject(request, projectPath, graphsByProject));
        }

        var versionsInUse = projects
            .Where(p => p.ResolvedVersion is not null)
            .GroupBy(p => VersionText.Normalize(p.ResolvedVersion!), StringComparer.OrdinalIgnoreCase)
            .Select(g => new PackageOriginVersionUsage(
                g.Key,
                [.. g.Select(p => p.ProjectName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]
            ))
            .ToList();

        var knownNames = request
            .Packages.References.Concat(request.Packages.GetDeclaredReferences())
            .Select(r => r.PackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new PackageOriginReport(
            request.PackageId,
            Found: projects.Any(p => p.Kind != PackageOriginKind.NotPresent),
            projects,
            versionsInUse,
            SuggestSimilar(request.PackageId, knownNames)
        );
    }

    private static PackageOriginProjectReport ClassifyProject(
        PackageOriginRequest request,
        string projectPath,
        Dictionary<string, ProjectResolvedGraph> graphsByProject
    )
    {
        var declarations = request
            .Packages.GetDeclaredReferences()
            .Where(r =>
                string.Equals(r.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                && r.PackageName.Equals(
                    request.PackageId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        // An Update cannot put a package into the graph; only an Include can. Prefer the strongest
        // fact: an Include decides the kind even when an Update also amends the reference.
        var includes = declarations.Where(r => !r.IsMetadataOnlyUpdate).ToList();
        string? inlineVersion = includes
            .Select(r => r.VersionOverride ?? r.Version)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var resolved = request
            .Packages.References.Where(r =>
                string.Equals(r.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                && r.PackageName.Equals(request.PackageId, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        var resolvedVersion = resolved
            .Select(r => r.Version)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        PackageOriginKind kind;
        if (includes.Count > 0)
        {
            kind = inlineVersion is not null
                ? PackageOriginKind.InlineVersion
                : PackageOriginKind.CentralPin;
        }
        else if (declarations.Count > 0)
        {
            kind = PackageOriginKind.UpdateOnly;
        }
        else
        {
            kind = resolved.Count > 0
                ? PackageOriginKind.TransitiveOnly
                : PackageOriginKind.NotPresent;
        }

        IReadOnlyList<string>? introducers = null;
        if (kind == PackageOriginKind.TransitiveOnly && graphsByProject.TryGetValue(projectPath, out var graph))
        {
            introducers = FindIntroducers(graph, request.PackageId);
        }

        return new PackageOriginProjectReport(
            projectPath,
            Path.GetFileName(projectPath),
            kind,
            inlineVersion,
            resolvedVersion is null ? null : VersionText.Normalize(resolvedVersion),
            introducers ?? []
        );
    }

    /// <summary>
    /// Names the direct packages whose dependency closure reaches the target, read from the graph's
    /// own edges rather than guessed from proximity.
    /// </summary>
    private static IReadOnlyList<string> FindIntroducers(ProjectResolvedGraph graph, string packageId)
    {
        SortedSet<string> introducers = new(StringComparer.OrdinalIgnoreCase);

        foreach (var packages in graph.Frameworks.Where(f => f.Resolved).Select(f => f.Packages))
        {
            var byId = packages
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            if (!byId.TryGetValue(packageId, out var target) || target.IsDirect)
            {
                continue;
            }

            var introducerIds = packages
                .Where(p => p.IsDirect)
                .Select(direct => direct.PackageId)
                .Where(id => Reaches(id, byId));
            foreach (var id in introducerIds)
            {
                introducers.Add(id);
            }
        }

        return [.. introducers];

        bool Reaches(string rootId, Dictionary<string, ResolvedPackage> byId)
        {
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new(byId.TryGetValue(rootId, out var root) ? root.Dependencies : []);

            while (pending.Count > 0)
            {
                var id = pending.Pop();
                if (id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!visited.Add(id) || !byId.TryGetValue(id, out var node))
                {
                    continue;
                }

                foreach (var dependency in node.Dependencies)
                {
                    pending.Push(dependency);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Near-miss package names for an unknown ID: case-insensitive edit distance within a small
    /// threshold, plus containment either way for long names. Ordered closest first, capped so a
    /// typo cannot bury the answer.
    /// </summary>
    internal static IReadOnlyList<string> SuggestSimilar(string query, IEnumerable<string> knownNames)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var needle = query.Trim().ToLowerInvariant();

        return
        [
            .. knownNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name =>
                {
                    var candidate = name.ToLowerInvariant();
                    return (Name: name, Distance: EditDistance(needle, candidate));
                })
                .Where(item =>
                    item.Distance <= 3
                    || item.Name.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                    || needle.Contains(item.Name.ToLowerInvariant(), StringComparison.Ordinal)
                )
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(item => item.Name),
        ];
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution =
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    substitution
                );
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private void Render(string packageId, PackageOriginReport report)
    {
        var root = new Tree(
            $"[bold {SpectrePalette.Ink.Primary}]{Markup.Escape(packageId)}[/]"
        )
        {
            Guide = TreeGuide.Line,
        };

        foreach (var group in report.Projects.GroupBy(p => p.Kind))
        {
            var (label, ink) = group.Key switch
            {
                PackageOriginKind.InlineVersion => ("declared directly (inline version)", SpectrePalette.Ink.Warning),
                PackageOriginKind.CentralPin => ("declared directly (central pin)", SpectrePalette.Ink.Success),
                PackageOriginKind.UpdateOnly => ("update-only amendment", SpectrePalette.Ink.Secondary),
                PackageOriginKind.TransitiveOnly => ("seen transitively", SpectrePalette.Ink.Muted),
                _ => ("not present", SpectrePalette.Ink.Dim),
            };

            var node = root.AddNode($"[{ink}]{label} ({group.Count()})[/]");
            foreach (var project in group.OrderBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase))
            {
                node.AddNode(RenderProject(project));
            }
        }

        AnsiConsole.Write(root);
        AnsiConsole.WriteLine();

        RenderDriftVerdict(report);

        var directCount = report.Projects.Count(p =>
            p.Kind is PackageOriginKind.InlineVersion or PackageOriginKind.CentralPin
        );
        var updateOnlyCount = report.Projects.Count(p => p.Kind == PackageOriginKind.UpdateOnly);
        var transitiveCount = report.Projects.Count(p => p.Kind == PackageOriginKind.TransitiveOnly);
        _console.Dim(
            $"  {report.Projects.Count} project(s): {directCount} direct, {updateOnlyCount} update-only, "
                + $"{transitiveCount} transitive."
        );
        _console.WriteLine();
    }

    private static string RenderProject(PackageOriginProjectReport project)
    {
        var nameInk = project.Kind switch
        {
            PackageOriginKind.InlineVersion => SpectrePalette.Ink.Warning,
            PackageOriginKind.CentralPin => SpectrePalette.Ink.Success,
            PackageOriginKind.UpdateOnly => SpectrePalette.Ink.Secondary,
            PackageOriginKind.TransitiveOnly => SpectrePalette.Ink.Muted,
            _ => SpectrePalette.Ink.Dim,
        };

        var detail = project.Kind switch
        {
            PackageOriginKind.InlineVersion =>
                $" [{SpectrePalette.Ink.Text}]{Markup.Escape(project.InlineVersion!)}[/]",
            PackageOriginKind.CentralPin =>
                $" [{SpectrePalette.Ink.Dim}]→ {Markup.Escape(project.ResolvedVersion ?? "(unresolved)")}[/]",
            PackageOriginKind.TransitiveOnly when project.TransitiveIntroducers!.Count > 0 =>
                $" [{SpectrePalette.Ink.Dim}]via {Markup.Escape(string.Join(", ", project.TransitiveIntroducers))}[/]",
            PackageOriginKind.TransitiveOnly => $" [{SpectrePalette.Ink.Dim}]introducer unknown[/]",
            _ => string.Empty,
        };

        return $"[{nameInk}]{Markup.Escape(project.ProjectName)}[/]{detail}";
    }

    private void RenderDriftVerdict(PackageOriginReport report)
    {
        var resolvedVersions = report.VersionsInUse;

        if (resolvedVersions.Count == 0)
        {
            _console.Dim("  No resolved version observed — nothing restored carries this package.");
            return;
        }

        if (resolvedVersions.Count == 1)
        {
            _console.Success(
                $"One version everywhere: {resolvedVersions[0].Version} "
                    + $"({resolvedVersions[0].ProjectNames.Count} project(s))."
            );
            return;
        }

        _console.Warning(
            $"Version drift: {resolvedVersions.Count} different versions are in use."
        );
        foreach (var usage in resolvedVersions.OrderByDescending(v => v.ProjectNames.Count))
        {
            _console.Dim($"  {usage.Version}: {string.Join(", ", usage.ProjectNames)}");
        }
    }
}
